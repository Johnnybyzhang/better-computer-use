using System.Text.Json;
using System.Text.Json.Nodes;

namespace BetterComputerUse;

internal sealed class McpServer(DesktopClient manager, bool allowElevation)
{
    private bool initialized;
    private bool ready;
    internal async Task RunAsync()
    {
        using var input = new StreamReader(Console.OpenStandardInput(), new System.Text.UTF8Encoding(false, true));
        using var output = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { AutoFlush = true };
        while (true)
        {
            JsonObject? request;
            var line = await Wire.ReadLineAsync(input, 1024 * 1024, CancellationToken.None);
            if (line is null) return;
            try { request = JsonNode.Parse(line) as JsonObject; }
            catch (JsonException) { await WriteError(null, -32700, "Parse error"); continue; }
            if (request is null || request["jsonrpc"]?.ToString() != "2.0" || request["method"] is not JsonValue methodValue || !methodValue.TryGetValue<string>(out var method) ||
                request["id"] is JsonValue idValue && !idValue.TryGetValue<string>(out _) && !idValue.TryGetValue<long>(out _))
            { await WriteError(request?["id"], -32600, "Invalid JSON-RPC request"); continue; }
            var id = request["id"];
            if (id is null)
            {
                if (method == "notifications/initialized" && initialized) ready = true;
                continue;
            }
            try
            {
                var adminToolsBefore = manager.AdminToolsAvailable;
                object result;
                var args = request["params"] as JsonObject ?? new JsonObject();
                if (method == "initialize")
                {
                    if (initialized) { await WriteError(id, -32600, "Already initialized"); continue; }
                    var requested = args["protocolVersion"]?.GetValue<string>();
                    var version = requested is "2024-11-05" or "2025-03-26" or "2025-06-18" or "2025-11-25" ? requested : "2025-11-25";
                    initialized = true;
                    result = new { protocolVersion = version, capabilities = new { tools = new { listChanged = true } },
                        serverInfo = new { name = "better-computer-use", version = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(McpServer).Assembly)?.InformationalVersion ?? "unknown" },
                        instructions = "Computer Use runs only in the managed Windows child session. Start a session, then pass its sessionId and generation on every call. Other agents can attach and take over using session_start or session_take_control. After handoff use the returned binding; stale input is rejected. Take over in the viewer pauses automation. No parent-desktop fallback." };
                }
                else if (method == "ping") result = new { };
                else if (!ready) { await WriteError(id, -32002, "Initialize and send notifications/initialized first"); continue; }
                else if (method == "tools/list") result = new { tools = Tools() };
                else if (method == "tools/call")
                {
                    var name = args.RequiredString("name");
                    if (!Tools().Any(t => t["name"]!.GetValue<string>() == name)) { await WriteError(id, -32602, "Unknown tool"); continue; }
                    try
                    {
                        var value = await CallAsync(name, args["arguments"] as JsonObject ?? new JsonObject());
                        var node = JsonSerializer.SerializeToNode(value, Wire.Json)!;
                        var content = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = node.ToJsonString(Wire.Json) } };
                        // Native helper screenshots are data URLs; expose them as standard MCP image blocks as well.
                        if (node["result"] is JsonObject helperResult && helperResult["screenshots"] is JsonArray screenshots)
                            foreach (var screenshot in screenshots.OfType<JsonObject>())
                            {
                                var url = screenshot["url"]?.GetValue<string>();
                                if (url is null || !url.StartsWith("data:image/", StringComparison.Ordinal)) continue;
                                var split = url.IndexOf(";base64,", StringComparison.Ordinal);
                                if (split > 0) content.Add(new JsonObject { ["type"] = "image", ["mimeType"] = url[5..split], ["data"] = url[(split + 8)..] });
                            }
                        result = new { content, isError = node["ok"]?.GetValue<bool>() == false };
                    }
                    catch (Exception ex)
                    { result = new { content = new[] { new { type = "text", text = ex.GetBaseException().Message } }, isError = true }; }
                }
                else { await WriteError(id, -32601, "Method not found"); continue; }
                await output.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }, Wire.Json));
                if (adminToolsBefore != manager.AdminToolsAvailable)
                    await output.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/tools/list_changed\"}");
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or JsonException)
            { await WriteError(id, -32602, ex.Message); }
        }

        Task WriteError(JsonNode? id, int code, string message) => output.WriteLineAsync(JsonSerializer.Serialize(new
            { jsonrpc = "2.0", id, error = new { code, message } }, Wire.Json));
    }

    private Task<object> CallAsync(string name, JsonObject args) => manager.CallAsync(name, args);

    private JsonObject[] Tools()
    {
        JsonObject Schema(JsonObject properties, params string[] required) => new() { ["type"] = "object", ["properties"] = properties,
            ["required"] = JsonSerializer.SerializeToNode(required), ["additionalProperties"] = false };
        JsonObject Type(string type, string? description = null) => new() { ["type"] = type, ["description"] = description };
        JsonObject Bound() => new() { ["sessionId"] = Type("integer"), ["generation"] = Type("string") };
        JsonObject Tool(string name, string description, JsonObject schema) => new() { ["name"] = name, ["description"] = description, ["inputSchema"] = schema };
        var start = new JsonObject { ["width"] = new JsonObject { ["type"] = "integer", ["minimum"] = 640, ["maximum"] = 4096 },
            ["height"] = new JsonObject { ["type"] = "integer", ["minimum"] = 480, ["maximum"] = 4096 },
            ["showViewer"] = Type("boolean"), ["enableChildSessions"] = Type("boolean", "Explicitly request WTSEnableChildSessions if disabled; native permissions still apply.") };
        start["mode"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("admin", "user"), ["default"] = "admin",
            ["description"] = "Admin requests main-session UAC for the Computer Use executable at session init. Declining UAC falls back to user mode. User mode has no administrator tools." };
        start["takeControl"] = Type("boolean", "Defaults true: transfer agent control. False attaches for observation without interrupting the current agent.");
        var stop = Bound(); stop["logoff"] = Type("boolean", "Defaults false (detach this agent, keep the desktop alive). True logs off the shared child session and closes its applications; unsaved work may be lost.");
        var view = Bound(); view["visible"] = Type("boolean");
        var computer = Bound();
        computer["method"] = new JsonObject { ["type"] = "string", ["enum"] = JsonSerializer.SerializeToNode(ComputerMethods.Allowed) };
        computer["params"] = Type("object", "Native helper parameters, e.g. {window:{app,id},include_text:true,include_screenshot:true}. Discover app/window IDs inside this session.");
        computer["meta"] = Type("object", "Optional turn metadata (session_id, turn_id, call_id). App approval stamps are rejected.");
        var process = Bound();
        process["executablePath"] = Type("string", "Absolute local path to the .exe to launch as administrator.");
        process["arguments"] = new JsonObject { ["type"] = "array", ["items"] = Type("string"), ["maxItems"] = 256 };
        process["workingDirectory"] = Type("string", "Optional existing absolute local directory; defaults to the executable directory.");
        var result = new List<JsonObject> {
            Tool("session_status", "Inspect manager state, Windows session IDs, worker/helper PIDs and executable hash.", Schema(new JsonObject())),
            Tool("session_start", "Create, attach to, or resume the shared child desktop. Defaults to PiP and transfers agent control; takeControl:false attaches for observation. Returns this client's binding. Existing applications stay open.", Schema(start)),
            Tool("session_take_control", "Transfer agent control to this client without closing applications. Human control remains paused until locally released. Returns a fresh binding; never override physical Escape interruption.", Schema(new JsonObject())),
            Tool("session_restart_worker", "Explicitly restart a failed worker without logging off applications. Returns a new generation; do not use to override a person's Escape interruption.", Schema(Bound(), "sessionId", "generation")),
            Tool("session_viewer", "Show or hide the floating RDP viewer. Human control is granted only by the local Take over button.", Schema(view, "sessionId", "generation")),
            Tool("session_stop", "Detach this agent while leaving the shared desktop running; logoff:true explicitly closes its applications.", Schema(stop, "sessionId", "generation")),
            Tool("session_logoff", "Recover before creating a fresh desktop: log off an existing disconnected child session only when no task owns its lock. Closes its applications and unsaved work. Use sessionId from session_status.existingChildSession, then session_start. No extra approval prompt is needed when replacing the abandoned desktop is within the user's task. Refuses connected, busy, changed or parent sessions.", Schema(new JsonObject { ["sessionId"] = Type("integer") }, "sessionId")),
            Tool("computer_use", "Call the installed Computer Use executable inside the bound child session. Paused during human control; never targets the parent desktop.", Schema(computer, "sessionId", "generation", "method")) };
        result.AddRange(ComputerMethods.Allowed.Select(NativeToolCatalog.Tool));
        if (manager.AdminToolsAvailable) result.Add(Tool("launch_process_as_admin", "Launch a specified .exe as administrator inside the owned child session. Windows UAC in the main session is the only approval prompt; returns verified PID/session/elevation. No automatic retries. The launched application stays open.",
            Schema(process, "sessionId", "generation", "executablePath")));
        if (allowElevation && manager.AdminToolsAvailable) result.Add(Tool("computer_use_elevated", "One elevated Computer Use request, with only Windows UAC in the main session. Same session binding and parameters as computer_use.",
            Schema((JsonObject)computer.DeepClone(), "sessionId", "generation", "method")));
        return result.ToArray();
    }
}

using System.Text.Json.Nodes;

namespace BetterComputerUse;

// Public Windows helper operations observed in the bundled @oai/sky client. Lifecycle close is
// owned by session_stop. The executable itself uses NDJSON, not an MCP tools/list endpoint.
internal static class NativeToolCatalog
{
    internal static JsonObject Tool(string method)
    {
        JsonObject Value(string type) => new() { ["type"] = type };
        var properties = new JsonObject { ["sessionId"] = Value("integer"), ["generation"] = Value("string"),
            ["meta"] = new JsonObject { ["type"] = "object", ["description"] = "Optional string session_id, turn_id, call_id, item_id; no approval stamps." } };
        var required = new List<string> { "sessionId", "generation" };
        void Field(string key, string type, bool needed = true)
        { properties[key] = Value(type); if (needed) required.Add(key); }
        if (method is not ("list_apps" or "list_windows" or "get_window" or "launch_app" or "start_audio_recording" or "stop_audio_recording" or "end_turn"))
        {
            properties["window"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject {
                ["app"] = Value("string"), ["id"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 }, ["title"] = Value("string") },
                ["required"] = new JsonArray("app", "id"), ["additionalProperties"] = false };
            required.Add("window");
        }
        switch (method)
        {
            case "get_window": Field("id", "integer"); Field("app", "string", false); break;
            case "launch_app": Field("app", "string"); break;
            case "get_window_state": Field("include_screenshot", "boolean", false); Field("include_text", "boolean", false); break;
            case "click": Field("x", "number"); Field("y", "number"); goto case "click-options";
            case "click_element": Field("element_index", "integer"); goto case "click-options";
            case "click-options": Field("click_count", "integer", false); Field("mouse_button", "string", false); Field("screenshotId", "string", false); break;
            case "scroll": foreach (var key in new[] { "x", "y", "scrollX", "scrollY" }) Field(key, "number"); Field("screenshotId", "string", false); break;
            case "drag": foreach (var key in new[] { "from_x", "from_y", "to_x", "to_y" }) Field(key, "number"); Field("screenshotId", "string", false); break;
            case "press_key": Field("key", "string"); break;
            case "type_text": Field("text", "string"); break;
            case "perform_secondary_action": Field("element_index", "integer"); Field("action", "string"); break;
            case "set_value": Field("element_index", "integer"); Field("value", "string"); break;
            case "start_audio_recording": Field("max_duration_ms", "integer", false); break;
        }
        var descriptions = new Dictionary<string, string> {
            ["get_window_state"] = "Capture screenshots and/or UI Automation text for a child-session window.",
            ["launch_app"] = "Launch an app by its discovered app ID inside the child session.",
            ["start_audio_recording"] = "Start native computer audio recording in the child session.",
            ["stop_audio_recording"] = "Stop audio recording and return the native audio file result.",
            ["end_turn"] = "End the native Computer Use turn. Does not release the desktop lock; session_stop does." };
        return new JsonObject { ["name"] = method, ["description"] = descriptions.GetValueOrDefault(method, $"Pass through native {method} in the bound child session."),
            ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = properties,
                ["required"] = System.Text.Json.JsonSerializer.SerializeToNode(required), ["additionalProperties"] = false } };
    }

    internal static JsonObject ForwardArguments(string method, JsonObject arguments)
    {
        var parameters = new JsonObject();
        foreach (var entry in arguments)
            if (entry.Key is not ("sessionId" or "generation" or "meta")) parameters[entry.Key] = entry.Value?.DeepClone();
        return new JsonObject { ["sessionId"] = arguments["sessionId"]?.DeepClone(), ["generation"] = arguments["generation"]?.DeepClone(),
            ["meta"] = arguments["meta"]?.DeepClone(), ["method"] = method, ["params"] = parameters };
    }
}

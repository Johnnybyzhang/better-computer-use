using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BetterComputerUse;

// The RDP desktop belongs to this detached process, not to an MCP transport.
internal static class DesktopHost
{
    internal static string PipeName => $"bcu-desktop-v1-{Native.UserSid}-{Native.CurrentSession}";

    internal static async Task RunAsync(SessionManager manager)
    {
        var service = new SharedDesktop(manager);
        NamedPipeServerStream Listen(bool first) => NamedPipeServerStreamAcl.Create(PipeName,
            PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : 0), 0, 0, AuthenticatedPipe.Security());
        var listener = Listen(true);
        while (true)
        {
            await listener.WaitForConnectionAsync();
            var connected = listener;
            listener = Listen(false); // Keep an instance alive: another host cannot take the name.
            _ = ServeAsync(connected, service);
        }
    }

    private static async Task ServeAsync(NamedPipeServerStream pipe, SharedDesktop service)
    {
        var client = Guid.NewGuid().ToString("N");
        using (pipe)
        try
        {
            using var handshakeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await Wire.ReadAsync(pipe, handshakeTimeout.Token);
            using var peer = Native.VerifyClient(pipe, Native.CurrentSession, DateTime.MinValue);
            if (Native.IsProcessElevated(peer.Id)) throw new UnauthorizedAccessException("Desktop clients must be unelevated.");
            await Wire.WriteAsync(pipe, new { connected = true, clientId = client }, handshakeTimeout.Token);
            while (true)
            {
                var request = await Wire.ReadAsync(pipe, default);
                try
                {
                    var result = await service.CallAsync(client, request.RequiredString("name"), request["arguments"] as JsonObject ?? new());
                    await Wire.WriteAsync(pipe, new { result, adminToolsAvailable = managerAdmin() }, default);
                }
                catch (Exception ex)
                {
                    await Wire.WriteAsync(pipe, new { error = ex.GetBaseException().Message, adminToolsAvailable = managerAdmin() }, default);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { /* A client leaving never tears down the desktop or replays its last action. */ }
        finally { await service.DetachAsync(client); }

        bool managerAdmin() => service.AdminToolsAvailable;
    }
}

internal sealed class DesktopClient(string[] hostArguments) : IDisposable
{
    private NamedPipeClientStream? pipe;
    internal bool AdminToolsAvailable { get; private set; } = true;

    private async Task ConnectAsync()
    {
        if (pipe is not null) return;
        NamedPipeClientStream Create() => new(".", DesktopHost.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Identification);
        var next = Create();
        try
        {
            try { await next.ConnectAsync(300); }
            catch (TimeoutException)
            {
                next.Dispose(); next = Create();
                // Launching a host is harmless if another client wins the first-instance race.
                using var started = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) {
                    UseShellExecute = false, CreateNoWindow = true,
                    Arguments = Launcher.Arguments(new[] { "desktop-host" }.Concat(hostArguments)) });
                await next.ConnectAsync(15000);
            }
            Native.Check(Native.GetNamedPipeServerProcessId(next.SafePipeHandle, out var pid));
            Native.VerifyServer(next, checked((int)pid), Native.CurrentSession, Native.ProcessStartUtc(checked((int)pid)).Ticks);
            if (Native.IsProcessElevated(checked((int)pid))) throw new UnauthorizedAccessException("Desktop host must be unelevated.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await Wire.WriteAsync(next, new { hello = true }, timeout.Token);
            await Wire.ReadAsync(next, timeout.Token);
            pipe = next;
        }
        catch { next.Dispose(); throw; }
    }

    internal async Task<object> CallAsync(string name, JsonObject args, bool retryConnection = true)
    {
        await ConnectAsync();
        JsonObject response;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await Wire.WriteAsync(pipe!, new { name, arguments = args }, timeout.Token);
            response = await Wire.ReadAsync(pipe!, timeout.Token);
        }
        catch
        {
            Dispose();
            // Reattaching is idempotent. Never retry an input, launch, or other native call.
            if (retryConnection && name is "session_start" or "session_status")
                return await CallAsync(name, args, retryConnection: false);
            throw new IOException("Desktop connection lost. Call session_start to reconnect. The previous operation was not replayed; inspect its outcome before continuing.");
        }
        AdminToolsAvailable = response["adminToolsAvailable"]?.GetValue<bool>() ?? false;
        if (response["error"] is JsonValue error) throw new InvalidOperationException(error.GetValue<string>());
        return response["result"]?.DeepClone() ?? new JsonObject();
    }

    public void Dispose() { pipe?.Dispose(); pipe = null; }
}

// Serializes requests across all clients. Handoffs invalidate earlier controller tokens.
internal sealed class DesktopControl
{
    private readonly Dictionary<string, Binding> clients = new();
    internal string? Controller { get; private set; }
    internal Binding Attach(string client, int session, bool takeControl)
    {
        if (!clients.TryGetValue(client, out var binding) || binding.SessionId != session)
            clients[client] = binding = new(session, Guid.NewGuid().ToString("N"));
        if (takeControl && Controller != client)
        {
            if (Controller is { } previous && clients.TryGetValue(previous, out var old))
                clients[previous] = new(old.SessionId, Guid.NewGuid().ToString("N"));
            Controller = client;
            clients[client] = binding = new(session, Guid.NewGuid().ToString("N"));
        }
        return binding;
    }
    internal Binding? BindingFor(string client) => clients.GetValueOrDefault(client);
    internal void Validate(string client, JsonObject args, bool input)
    {
        (BindingFor(client) ?? throw new InvalidOperationException("Call session_start to attach to the desktop.")).Validate(args);
        if (input && Controller != client)
            throw new InvalidOperationException("Control moved to another agent. Call session_take_control to take over, then use the returned binding.");
    }
    internal void Detach(string client) { clients.Remove(client); if (Controller == client) Controller = null; }
    internal void Reset() { clients.Clear(); Controller = null; }
}

internal sealed class SharedDesktop(SessionManager manager)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly DesktopControl control = new();
    internal bool AdminToolsAvailable => manager.AdminToolsAvailable;
    private string? workerGeneration;

    internal async Task DetachAsync(string client)
    {
        await gate.WaitAsync();
        try { control.Detach(client); await manager.SetAgentConnectedAsync(control.Controller is not null); }
        finally { gate.Release(); }
    }

    internal async Task<JsonObject> CallAsync(string client, string name, JsonObject args)
    {
        await gate.WaitAsync();
        try
        {
            JsonObject Node(object value) => JsonSerializer.SerializeToNode(value, Wire.Json)!.AsObject();
            var status = Node(await manager.StatusAsync());
            if (workerGeneration != status["generation"]?.GetValue<string>())
            { control.Reset(); workerGeneration = status["generation"]?.GetValue<string>(); }
            if (name == "session_status") return Describe(status);
            if (name is "session_start" or "session_take_control")
            {
                var started = Node(await manager.StartAsync(args["width"]?.GetValue<int>() ?? 1920,
                    args["height"]?.GetValue<int>() ?? 1080, args["showViewer"]?.GetValue<bool>() ?? true,
                    args["enableChildSessions"]?.GetValue<bool>() ?? false, args["mode"]?.GetValue<string>() ?? "admin"));
                var generation = started["generation"]!.GetValue<string>();
                if (workerGeneration != generation) { control.Reset(); workerGeneration = generation; }
                control.Attach(client, started["sessionId"]!.GetValue<int>(), name == "session_take_control" || (args["takeControl"]?.GetValue<bool>() ?? true));
                await manager.SetAgentConnectedAsync(control.Controller is not null);
                return Describe(started);
            }
            if (name == "session_logoff") return Node(await manager.LogoffDisconnectedAsync(args));
            var readOnly = name is "list_apps" or "list_windows" or "get_window" or "get_window_state" ||
                name == "computer_use" && args["method"]?.GetValue<string>() is "list_apps" or "list_windows" or "get_window" or "get_window_state";
            if (name == "session_stop" && args["logoff"]?.GetValue<bool>() != true)
            {
                // Detach affects only this transport; a handoff must not make cleanup fail.
                control.Detach(client);
                await manager.SetAgentConnectedAsync(control.Controller is not null);
                return new() { ["detached"] = true, ["loggedOff"] = false, ["state"] = status["state"]?.DeepClone() };
            }
            control.Validate(client, args, !readOnly);
            var bound = (JsonObject)args.DeepClone();
            bound["sessionId"] = status["sessionId"]?.DeepClone();
            bound["generation"] = status["generation"]?.DeepClone();
            var result = Node(await SessionCommands.CallAsync(manager, name, bound));
            if (name is "session_restart_worker" or "session_stop")
            {
                control.Reset();
                workerGeneration = result["generation"]?.GetValue<string>();
                if (result["sessionId"] is JsonValue id) control.Attach(client, id.GetValue<int>(), true);
                await manager.SetAgentConnectedAsync(control.Controller is not null);
                return Describe(result);
            }
            return result;

            JsonObject Describe(JsonObject value)
            {
                value["generation"] = control.BindingFor(client)?.Generation;
                value["clientId"] = client;
                value["controllerId"] = control.Controller;
                value["hasControl"] = control.Controller == client;
                value["hostPid"] = Environment.ProcessId;
                return value;
            }
        }
        finally { gate.Release(); }
    }
}

using System.Text.Json.Nodes;

namespace BetterComputerUse;

internal sealed class SessionManager(Control dispatcher, HelperIdentity? helper, bool allowElevation) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private RdpWindow? viewer;
    private WorkerConnection? worker;
    private Binding? binding;
    private DesktopLease? desktopLease;
    private string state = "stopped";
    private string? lastError;
    private string workerMode = "uninitialized";
    private bool uacDeclined;
    private bool agentConnected;
    private int desktopWidth = 1920, desktopHeight = 1080;
    internal bool AdminToolsAvailable => workerMode != "user";
    internal bool ElevatedComputerAvailable => allowElevation && AdminToolsAvailable;
    private volatile bool humanControl;
    private volatile bool connectionLost;
    private long controlRevision;
    private readonly CancellationTokenSource lifetime = new();

    private async Task<T> Ui<T>(Func<T> action) => await dispatcher.InvokeAsync(action, lifetime.Token);
    private async Task Ui(Action action) => await dispatcher.InvokeAsync(action, lifetime.Token);

    internal Task SetAgentConnectedAsync(bool connected) => Ui(() =>
    {
        agentConnected = connected;
        viewer?.SetAgentConnected(connected);
    });

    internal async Task<object> StatusAsync()
    {
        await gate.WaitAsync();
        try
        {
            return new { state = connectionLost || worker is not null && !worker.IsAlive ? "faulted" : state,
                parentSessionId = Native.CurrentSession, sessionId = binding?.SessionId, generation = binding?.Generation,
                workerPid = worker?.WorkerPid, helperPid = worker?.HelperPid, helper,
                humanControl, viewerVisible = viewer is not null && await Ui(() => viewer.ViewerVisible),
                viewerMode = viewer is null ? "hidden" : await Ui(() => viewer.ViewMode),
                elevationEnabled = workerMode == "admin", mode = workerMode, uacDeclined, lastError,
                existingChildSession = desktopLease is null ? DisconnectedSessionRecovery.Windows().Inspect() : new { sessionId = binding?.SessionId, canLogoff = false, reason = "Shared desktop is managed here. Other agents can attach with session_start or take over with session_take_control." } };
        }
        finally { gate.Release(); }
    }

    internal async Task<object> StartAsync(int width, int height, bool show, bool enable, string mode = "admin")
    {
        if (mode is not ("admin" or "user")) throw new ArgumentException("mode must be admin or user.");
        if (width is < 640 or > 4096 || height is < 480 or > 4096) throw new ArgumentException("Desktop dimensions must be 640..4096 by 480..4096.");
        await gate.WaitAsync(lifetime.Token);
        var startupBegun = false;
        DesktopLease? acquiredLease = null;
        try
        {
            if (viewer is not null && !connectionLost && worker is { IsAlive: true } && state == "ready")
            {
                if (!humanControl) await Ui(() => viewer.SetViewer(show));
                return new { state, binding!.SessionId, binding.Generation, workerPid = worker.WorkerPid,
                    helperPid = worker.HelperPid, mode = workerMode, helperElevated = workerMode == "admin", uacDeclined };
            }
            if (humanControl) throw new InvalidOperationException("Desktop recovery is waiting for you to return control in the viewer.");
            if (viewer is not null)
            {
                width = desktopWidth; height = desktopHeight; mode = workerMode;
                worker?.Dispose(); worker = null;
                await Ui(() => viewer.CloseHost()); viewer = null;
                desktopLease?.Dispose(); desktopLease = null;
            }
            if (helper is null) throw new InvalidOperationException("No installed Computer Use helper was found. Initialize Computer Use in the ChatGPT/Codex app, then restart this MCP server; or launch with --helper <installed codex-computer-use.exe>.");
            helper.Verify();
            if (Native.CurrentSession == 0 || Native.Elevated) throw new InvalidOperationException("Start the manager unelevated in an interactive Windows session.");
            acquiredLease = DesktopLease.Acquire();
            desktopLease = acquiredLease;
            var existing = Native.ChildSession();
            if (existing is int existingId)
            {
                Native.VerifyChildSession(existingId);
                if (DesktopProfile.Load(existingId) is { } profile)
                { width = profile.Width; height = profile.Height; }
                using var settling = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                settling.CancelAfter(TimeSpan.FromSeconds(10));
                while (Native.SessionConnectionState(existingId) != 4)
                {
                    Native.VerifyChildSession(existingId);
                    await Task.Delay(100, settling.Token);
                }
            }
            Native.Check(Native.WTSIsChildSessionsEnabled(out var enabled));
            if (!enabled)
            {
                if (!enable) throw new InvalidOperationException("Windows child sessions are disabled. Set enableChildSessions=true to request the native enable operation.");
                Native.Check(Native.WTSEnableChildSessions(true));
            }
            startupBegun = true;
            workerMode = mode; uacDeclined = false;
            desktopWidth = width; desktopHeight = height;
            state = "connecting"; connectionLost = false; lastError = null;
            viewer = await Ui(() =>
            {
                var window = new RdpWindow(desktopSize: new Size(width, height));
                viewer = window;
                window.Rdp.Lost += reason => OnViewerLost(window, reason);
                window.ControlRequested += requested => _ = ChangeControlAsync(window, requested);
                try
                {
                    window.Show(); window.SetHumanControl(false); window.SetAgentConnected(agentConnected); window.SetConnectionStatus("Connecting..."); window.Rdp.Connect(width, height);
                    window.SetViewer(show);
                    return window;
                }
                catch { window.Dispose(); throw; }
            });
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            await viewer.Rdp.Login.Task.WaitAsync(timeout.Token);
            var child = Native.ChildSession() ?? throw new InvalidOperationException("RDP logged in without a child session ID.");
            Native.VerifyChildSession(child);
            if (existing is not null && existing != child) throw new InvalidOperationException("Child session changed during reconnect; refusing to route input.");
            binding = new Binding(child, Guid.NewGuid().ToString("N"));
            new DesktopProfile(child, width, height).Save();
            state = "starting-worker";
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            worker = await StartSessionWorkerAsync(timeout.Token);
            if (connectionLost) throw new IOException("RDP disconnected during worker startup.");
            state = "ready";
            await Ui(() => viewer!.SetConnectionStatus(null));
            return new { state, binding.SessionId, binding.Generation, workerPid = worker.WorkerPid, helperPid = worker.HelperPid,
                mode = workerMode, helperElevated = workerMode == "admin", uacDeclined };
        }
        catch (Exception ex)
        {
            try
            {
                if (!startupBegun) throw;
                lastError = ex.GetBaseException().Message; state = "faulted";
                worker?.Dispose(); worker = null;
                if (viewer is not null) { await Ui(() => viewer.CloseHost()); viewer = null; }
                // Never log off a session on an ambiguous startup failure.
            }
            finally
            {
                acquiredLease?.Dispose();
                if (ReferenceEquals(desktopLease, acquiredLease)) desktopLease = null;
            }
            throw;
        }
        finally { gate.Release(); }
    }

    private void OnViewerLost(RdpWindow source, string reason)
    {
        if (!ReferenceEquals(viewer, source)) return;
        connectionLost = true; lastError = reason;
        source.SetConnectionStatus("Disconnected - reconnect to resume"); worker?.Dispose();
    }

    private async Task ChangeControlAsync(RdpWindow source, bool requested)
    {
        if (!ReferenceEquals(viewer, source)) return;
        var revision = Interlocked.Increment(ref controlRevision);
        // Human takeover prevents newly arriving operations before waiting for the in-flight operation.
        if (requested) humanControl = true;
        await gate.WaitAsync();
        try
        {
            if (revision != Interlocked.Read(ref controlRevision)) return;
            if (!ReferenceEquals(viewer, source)) { humanControl = false; return; }
            if (requested && (connectionLost || state != "ready" || !await Ui(() => viewer.ViewerVisible))) { humanControl = false; await Ui(() => viewer.SetHumanControl(false)); return; }
            await Ui(() => viewer.SetHumanControl(requested));
            humanControl = requested;
        }
        catch (Exception ex) { lastError = ex.Message; }
        finally { gate.Release(); }
    }

    internal async Task<object> RestartWorkerAsync(JsonObject args)
    {
        await gate.WaitAsync(lifetime.Token);
        try
        {
            if (binding is null || viewer is null || connectionLost || !await Ui(() => viewer.Rdp.Connected))
                throw new InvalidOperationException("An owned connected RDP session is required to restart its worker.");
            binding.Validate(args);
            if (Native.ChildSession() != binding.SessionId) throw new InvalidOperationException("Windows child session changed.");
            if (humanControl) throw new InvalidOperationException("A person has control. Return to View first.");
            helper!.Verify();
            worker?.Dispose(); worker = null;
            binding = new Binding(binding.SessionId, Guid.NewGuid().ToString("N"));
            state = "starting-worker";
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            worker = await StartSessionWorkerAsync(timeout.Token);
            state = "ready"; lastError = null;
            return new { state, binding.SessionId, binding.Generation, workerPid = worker.WorkerPid, helperPid = worker.HelperPid,
                mode = workerMode, helperElevated = workerMode == "admin", uacDeclined };
        }
        catch (Exception ex)
        {
            lastError = ex.GetBaseException().Message;
            if (state == "starting-worker") { state = "faulted"; worker?.Dispose(); worker = null; }
            throw;
        }
        finally { gate.Release(); }
    }

    private void Validate(JsonObject args)
    {
        if (binding is null || worker is null || state != "ready" || connectionLost || !worker.IsAlive)
            throw new InvalidOperationException("Child-session worker is not ready.");
        binding.Validate(args);
        if (Native.ChildSession() != binding.SessionId) throw new InvalidOperationException("Windows child session changed; refusing to route the request.");
    }

    internal async Task<object> ViewerAsync(JsonObject args)
    {
        await gate.WaitAsync();
        try
        {
            Validate(args);
            var visible = args["visible"]?.GetValue<bool>() ?? true;
            if (!visible && humanControl) throw new InvalidOperationException("A person has control. Only the local View button or closing the viewer can release control.");
            await Ui(() => viewer!.SetViewer(visible));
            if (!visible) humanControl = false;
            return new { visible, humanControl };
        }
        finally { gate.Release(); }
    }

    private async Task<WorkerConnection> StartSessionWorkerAsync(CancellationToken ct)
    {
        var result = await SessionWorkerStartup.StartAsync(workerMode,
            () => StartElevatedWorkerAsync(ct, persistent: true), () => StartUserWorkerAsync(ct));
        workerMode = result.Mode; uacDeclined |= result.UacDeclined;
        return result.Worker;
    }

    private async Task<WorkerConnection> StartUserWorkerAsync(CancellationToken ct)
    {
        var connection = new WorkerConnection(binding!);
        try
        {
            await Task.Run(() => Launcher.LaunchStandard(connection.PipeName, binding!)).WaitAsync(ct);
            await connection.AcceptAsync(helper!, false, ct);
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    private async Task<WorkerConnection> StartElevatedWorkerAsync(CancellationToken ct, bool persistent = false)
    {
        var connection = new WorkerConnection(binding!);
        try
        {
            await ElevationBroker.SpawnWorkerAsync(binding!, connection.PipeName,
                start => Ui(() => System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Windows returned no elevation broker process.")), ct, persistent);
            try { await connection.AcceptAsync(helper!, true, ct); }
            catch (Exception ex) { throw new InvalidOperationException($"Authenticating elevated child worker in session {binding!.SessionId}: {ex.Message}. No application launch request was sent."); }
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    internal async Task<JsonObject> LaunchProcessAsAdminAsync(JsonObject args)
    {
        if (!AdminToolsAvailable) throw new InvalidOperationException("Administrator tools are unavailable in user-space mode.");
        if (humanControl) throw new InvalidOperationException("A person has control. Return to View first.");
        await gate.WaitAsync(lifetime.Token);
        try
        {
            Validate(args);
            if (humanControl) throw new InvalidOperationException("Human takeover is pending.");
            var spec = ProcessLaunchSpec.FromArguments(args);
            Validate(args);
            if (humanControl) throw new InvalidOperationException("Human takeover is pending.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            using var elevatedWorker = await StartElevatedWorkerAsync(timeout.Token);
            Validate(args);
            if (humanControl) throw new InvalidOperationException("Human takeover is pending; process was not launched.");
            return await elevatedWorker.SendAsync(new JsonObject { ["operation"] = "launch_process_as_admin",
                ["launch"] = System.Text.Json.JsonSerializer.SerializeToNode(spec, Wire.Json) }, timeout.Token);
        }
        finally { gate.Release(); }
    }

    internal async Task<JsonObject> ComputerAsync(JsonObject args, bool elevated)
    {
        var observation = args["method"]?.GetValue<string>() is "list_apps" or "list_windows" or "get_window" or "get_window_state";
        if (elevated && !AdminToolsAvailable) throw new InvalidOperationException("Administrator tools are unavailable in user-space mode.");
        if (humanControl && !observation) throw new InvalidOperationException("A person has control. Use View in the viewer to resume automation.");
        await gate.WaitAsync(lifetime.Token);
        WorkerConnection? oneShot = null;
        try
        {
            Validate(args);
            if (humanControl && !observation) throw new InvalidOperationException("Human takeover is pending.");
            var method = args.RequiredString("method");
            var parameters = args["params"] as JsonObject ?? new JsonObject();
            ComputerMethods.Validate(method, parameters);
            var meta = args["meta"]?.DeepClone() as JsonObject ?? new JsonObject();
            foreach (var item in meta)
                if (item.Key is not ("session_id" or "turn_id" or "call_id" or "item_id") || item.Value is not JsonValue value || !value.TryGetValue<string>(out _))
                    throw new ArgumentException("meta accepts only string session_id, turn_id, call_id and item_id; approval stamps cannot be supplied through MCP.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            var connection = worker!;
            if (elevated)
            {
                if (!allowElevation) throw new InvalidOperationException("Elevation is disabled. Start with --allow-elevation to expose this capability.");
                Validate(args);
                oneShot = await StartElevatedWorkerAsync(timeout.Token);
                connection = oneShot;
            }
            return await NativeAppAuthorization.ExecuteAsync(meta, async authorizedMeta =>
            {
                Validate(args);
                if (humanControl && !observation) throw new InvalidOperationException("Human takeover is pending.");
                return await connection.SendAsync(new JsonObject { ["operation"] = "computer", ["method"] = method,
                    ["params"] = parameters.DeepClone(), ["meta"] = authorizedMeta.DeepClone() }, timeout.Token);
            });
        }
        finally { oneShot?.Dispose(); gate.Release(); }
    }

    internal async Task<object> StopAsync(JsonObject args)
    {
        await gate.WaitAsync();
        try
        {
            if (binding is null) throw new InvalidOperationException("No owned session binding is available.");
            binding.Validate(args);
            var logoff = args["logoff"]?.GetValue<bool>() ?? false;
            if (logoff && humanControl) throw new InvalidOperationException("Return human control before ending the desktop.");
            if (logoff && Native.ChildSession() != binding.SessionId) throw new InvalidOperationException("Session changed; refusing logoff.");
            state = "stopping";
            worker?.Dispose(); worker = null;
            if (logoff)
            {
                Native.Check(Native.WTSLogoffSession(0, (uint)binding.SessionId, false));
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                while (Native.ChildSession() == binding.SessionId) await Task.Delay(100, timeout.Token);
                DesktopProfile.Clear();
            }
            if (viewer is not null) { await Ui(() => viewer.CloseHost(disconnect: !logoff)); viewer = null; }
            desktopLease?.Dispose(); desktopLease = null;
            binding = null; state = "stopped"; humanControl = false; connectionLost = false;
            return new { state, loggedOff = logoff };
        }
        catch (Exception ex)
        {
            if (state == "stopping") { state = "faulted"; lastError = ex.GetBaseException().Message; }
            throw;
        }
        finally { gate.Release(); }
    }

    internal async Task<object> LogoffDisconnectedAsync(JsonObject args)
    {
        await gate.WaitAsync(lifetime.Token);
        try
        {
            if (viewer is not null || worker is not null || desktopLease is not null)
                throw new InvalidOperationException("This task manages a session. Use session_stop with logoff:true and its binding.");
            var result = await DisconnectedSessionRecovery.Windows().LogoffAsync(
                args["sessionId"]?.GetValue<int>() ?? throw new ArgumentException("sessionId from session_status is required."), lifetime.Token);
            DesktopProfile.Clear();
            binding = null; state = "stopped"; lastError = null; humanControl = false; connectionLost = false;
            return result;
        }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            worker?.Dispose(); worker = null;
            if (viewer is not null) { await Ui(() => viewer.CloseHost()); viewer = null; }
            lifetime.Cancel();
        }
        finally { desktopLease?.Dispose(); desktopLease = null; gate.Release(); lifetime.Dispose(); }
    }
}

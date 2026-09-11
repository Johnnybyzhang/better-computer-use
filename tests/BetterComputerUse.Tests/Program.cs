using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json.Nodes;
using BetterComputerUse;

// Run UI regressions on the child desktop without stealing the user's parent desktop.
if (args.FirstOrDefault() == "--launch-child-suite")
{
    var session = Native.ChildSession() ?? throw new Exception("Start the test desktop first.");
    Native.VerifyChildSession(session);
    Launcher.LaunchTask(["--child-suite", Path.GetFullPath(args[1])], new Binding(session, "test-suite"), false);
    return;
}
if (args.FirstOrDefault() == "--child-suite")
{
    Native.FreeConsole();
    var report = new StreamWriter(args[1], false, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
    Console.SetOut(report); Console.SetError(report);
}

// Opt-in process whose document exists in a TextBox, for live reconnect/crash tests.
if (args.FirstOrDefault() == "--launch-desktop-document")
{
    var session = Native.ChildSession() ?? throw new Exception("Start the test desktop first.");
    Native.VerifyChildSession(session);
    Launcher.LaunchTask(["--desktop-document", Path.GetFullPath(args[1]), args[2]], new Binding(session, "probe"), false);
    return;
}
if (args.FirstOrDefault() == "--desktop-document")
{
    Native.FreeConsole();
    var thread = new Thread(() =>
    {
        using var form = new System.Windows.Forms.Form { Text = "BCU unsaved reconnect test", Width = 700, Height = 450 };
        var editor = new System.Windows.Forms.TextBox { Multiline = true, Dock = System.Windows.Forms.DockStyle.Fill, Text = args[2] };
        form.Controls.Add(editor);
        using var timer = new System.Windows.Forms.Timer { Interval = 100 };
        timer.Tick += (_, _) =>
        {
            if (File.Exists(args[1] + ".close")) { form.Close(); return; }
            if (File.Exists(args[1] + ".query"))
            {
                File.Delete(args[1] + ".query");
                File.WriteAllText(args[1], System.Text.Json.JsonSerializer.Serialize(new { pid = Environment.ProcessId,
                    sessionId = Native.CurrentSession, text = editor.Text, sampledAt = DateTime.UtcNow }));
            }
        };
        timer.Start(); System.Windows.Forms.Application.Run(form);
    });
    thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join(); return;
}

if (args.FirstOrDefault() == "elevation-broker") { await ElevationBroker.RunAsync(args); return; }
if (args.FirstOrDefault() == "elevated-worker") { await Worker.RunAsync(args, true); return; }
if (args.FirstOrDefault() == "--admin-child-probe")
{
    using var lease = DesktopLease.Acquire();
    var binding = new Binding(Native.ChildSession() ?? throw new Exception("No existing child desktop; probe will not create or log off one."), Guid.NewGuid().ToString("N"));
    using var connection = new WorkerConnection(binding);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    var marker = Path.Combine(Path.GetTempPath(), "bcu-admin-marker-" + Guid.NewGuid().ToString("N"));
    Console.WriteLine($"Probe main={Native.CurrentSession} child={binding.SessionId}");
    await ElevationBroker.SpawnWorkerAsync(binding, connection.PipeName, start => Task.FromResult(Process.Start(start)!), timeout.Token);
    Console.WriteLine("Broker scheduled elevated worker");
    var probeHelper = args.Contains("--real-helper") ? HelperIdentity.FindInstalled() ?? throw new Exception("Installed helper missing") : HelperIdentity.Load(Environment.ProcessPath!);
    await connection.AcceptAsync(probeHelper, true, timeout.Token);
    Console.WriteLine($"Authenticated elevated worker={connection.WorkerPid}, helper={connection.HelperPid}");
    var spec = new ProcessLaunchSpec(HelperIdentity.Load(Environment.ProcessPath!), ["--launch-marker", marker], AppContext.BaseDirectory);
    var response = await connection.SendAsync(new JsonObject { ["operation"] = "launch_process_as_admin",
        ["launch"] = System.Text.Json.JsonSerializer.SerializeToNode(spec, Wire.Json) }, timeout.Token);
    Console.WriteLine(response.ToJsonString());
    if (response["ok"]?.GetValue<bool>() != true) throw new Exception("Launch failed");
    while (!File.Exists(marker)) await Task.Delay(50, timeout.Token);
    Console.WriteLine("Child marker: " + File.ReadAllText(marker));
    File.Delete(marker);
    return;
}

// Opt-in real UAC test; touches no existing desktop, application or capture.
if (args.FirstOrDefault() == "--uac-ipc-client")
{
    foreach (var level in new[] { System.Security.Principal.TokenImpersonationLevel.Impersonation, System.Security.Principal.TokenImpersonationLevel.Identification })
    {
        using var pipe = new NamedPipeClientStream(".", args[1] + "-" + level, PipeDirection.InOut, PipeOptions.Asynchronous, level);
        await pipe.ConnectAsync(20000);
        await Wire.WriteAsync(pipe, new { pid = Environment.ProcessId }, default);
        await Wire.ReadAsync(pipe, default).WaitAsync(TimeSpan.FromSeconds(30));
    }
    return;
}
if (args.FirstOrDefault() == "--uac-ipc-probe")
{
    var name = "bcu-uac-test-" + Guid.NewGuid().ToString("N");
    using var oldPipe = AuthenticatedPipe.CreateServer(name + "-Impersonation");
    using var newPipe = AuthenticatedPipe.CreateServer(name + "-Identification");
    var earliest = DateTime.UtcNow.AddSeconds(-1);
    using var elevated = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) {
        UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
        Arguments = Launcher.Arguments(["--uac-ipc-client", name]) })!;
    foreach (var pipe in new[] { oldPipe, newPipe })
    {
        var stage = "connection";
        try
        {
            await pipe.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(60));
            await Wire.ReadAsync(pipe, default).WaitAsync(TimeSpan.FromSeconds(10));
            stage = "VerifyClient";
            using var verified = Native.VerifyClient(pipe, Native.CurrentSession, earliest);
            stage = "elevation token query";
            if (verified.Id != elevated.Id || !Native.IsProcessElevated(verified.Id)) throw new Exception("Elevation mismatch");
            Console.WriteLine((pipe == oldPipe ? "Impersonation" : "Identification") + ": PASS");
        }
        catch (Exception ex) { Console.WriteLine((pipe == oldPipe ? "Impersonation" : "Identification") + $": FAIL at {stage}: {ex}"); }
        finally { if (pipe.IsConnected) await Wire.WriteAsync(pipe, new { done = true }, default); }
    }
    await elevated.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    return;
}

if (args.FirstOrDefault() == "--launch-marker")
{
    File.WriteAllText(args[1], args.Length == 2 ? $"session={Native.CurrentSession}; elevated={Native.Elevated}" : string.Join("|", args.Skip(2)));
    return;
}

if (args.FirstOrDefault() == "--parent-pid")
{
    while (Console.ReadLine() is { } line)
    {
        var request = JsonNode.Parse(line)!.AsObject();
        var method = request["method"]!.GetValue<string>();
        if (method == "hang") { await Task.Delay(Timeout.Infinite); return; }
        if (method == "bad-id") request["id"] = -1;
        Console.WriteLine(new JsonObject { ["id"] = request["id"]!.DeepClone(), ["ok"] = method != "approval",
            ["result"] = method == "approval" ? null : request["params"]?.DeepClone(),
            ["approvalRequest"] = method == "approval" ? new JsonObject { ["app"] = "test-app", ["displayName"] = "Test App" } : null }.ToJsonString());
    }
    return;
}

var count = 0;
var skipped = 0;
var ci = args.Contains("--ci");
async Task Test(string name, Func<Task> action)
{
    if (ci && (name.StartsWith("RDP ") || name.StartsWith("View ") ||
        name.StartsWith("suspended application") || name.StartsWith("verified suspended") ||
        name.StartsWith("elevation broker refuses an unelevated") || name.StartsWith("default helper startup refuses")))
    { skipped++; Console.WriteLine("SKIP interactive/unelevated-host check: " + name); return; }
    await action(); count++; Console.WriteLine("PASS " + name);
}
void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
Task Sync(Action action) { action(); return Task.CompletedTask; }
Task Sta(Action action)
{
    var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var thread = new Thread(() => { try { action(); completed.SetResult(); } catch (Exception ex) { completed.SetException(ex); } });
    thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completed.Task;
}

await Test("binding accepts exact session and generation", () => Sync(() => new Binding(2, "one").Validate(new JsonObject { ["sessionId"] = 2, ["generation"] = "one" })));
await Test("recycled session ID does not accept old generation", () => Throws<InvalidOperationException>(() => Sync(() => new Binding(2, "new").Validate(new JsonObject { ["sessionId"] = 2, ["generation"] = "old" }))));
await Test("parent session binding rejected", () => Throws<InvalidOperationException>(() => Sync(() => new Binding(2, "one").Validate(new JsonObject { ["sessionId"] = 1, ["generation"] = "one" }))));
await Test("arbitrary helper methods are rejected", () => Throws<ArgumentException>(() => Sync(() => ComputerMethods.Validate("execute_shell", new JsonObject()))));
await Test("window methods require a session-local window identity", () => Throws<ArgumentException>(() => Sync(() => ComputerMethods.Validate("click", new JsonObject()))));
await Test("framed IPC roundtrip preserves Unicode", async () =>
{
    using var stream = new MemoryStream(); await Wire.WriteAsync(stream, new { text = "中文 🖥️", value = 3 }, default);
    stream.Position = 0; Assert((await Wire.ReadAsync(stream, default))["text"]!.GetValue<string>() == "中文 🖥️");
});
await Test("oversized frame rejected before payload allocation", () => Throws<InvalidDataException>(async () =>
{ using var stream = new MemoryStream(BitConverter.GetBytes(Wire.MaxFrame + 1)); await Wire.ReadAsync(stream, default); }));
await Test("truncated IPC header rejected", () => Throws<EndOfStreamException>(async () =>
{ using var stream = new MemoryStream([1, 2]); await Wire.ReadAsync(stream, default); }));
await Test("bounded NDJSON rejects excessive line", () => Throws<InvalidDataException>(async () =>
{ using var reader = new StringReader("12345\n"); await Wire.ReadLineAsync(reader, 4, default); }));
await Test("truncated NDJSON is not executed", () => Throws<EndOfStreamException>(async () =>
{ using var reader = new StringReader("{}"); await Wire.ReadLineAsync(reader, 4, default); }));
await Test("Windows quoting handles quotes and trailing slashes", () => Sync(() =>
{ Assert(Launcher.Quote("a b\\") == "\"a b\\\\\""); Assert(Launcher.Quote("a\"b") == "\"a\\\"b\""); }));
await Test("worker refuses parent desktop before opening IPC or helper", () => Throws<UnauthorizedAccessException>(() => Worker.RunAsync(
    ["worker", "unused", Native.CurrentSession.ToString(), Environment.ProcessId.ToString(), Native.CurrentSession.ToString(), "0", "test"], false)));

await Test("OS process image and elevation queries match this process", () => Sync(() =>
{
    Assert(Native.ProcessImage(Environment.ProcessId) == Environment.ProcessPath);
    Assert(Native.IsProcessElevated(Environment.ProcessId) == Native.Elevated);
}));
await Test("RDP continuing logon does not fail the connection", () => Sta(() =>
{
    using var rdp = new RdpControl(); var events = new RdpEvents(rdp);
    events.OnLogonError(-2); Assert(!rdp.Login.Task.IsCompleted);
    events.OnLoginComplete(); Assert(rdp.Login.Task.IsCompletedSuccessfully);
}));
await Test("RDP access denial is terminal", () => Sta(() =>
{
    using var rdp = new RdpControl(); new RdpEvents(rdp).OnLogonError(-1);
    Assert(rdp.Login.Task.IsFaulted); _ = rdp.Login.Task.Exception;
}));

var identity = HelperIdentity.Load(Environment.ProcessPath!);
await Test("native app challenge is authorized without a dialog and preserves caller metadata", async () =>
{
    var meta = new JsonObject { ["turn_id"] = "turn" };
    int calls = 0;
    var result = await NativeAppAuthorization.ExecuteAsync(meta, sent =>
    {
        calls++;
        Assert(sent["turn_id"]!.GetValue<string>() == "turn");
        if (calls == 1) return Task.FromResult(new JsonObject { ["ok"] = false, ["approvalRequest"] = new JsonObject { ["app"] = "dswave.exe" } });
        Assert(sent["x-oai-cua-approved-app"]!.GetValue<string>() == "dswave.exe");
        return Task.FromResult(new JsonObject { ["ok"] = true, ["result"] = "clicked" });
    });
    Assert(calls == 2 && result["ok"]!.GetValue<bool>() && meta["x-oai-cua-approved-app"] is null);
});
await Test("ordinary native errors are not retried", async () =>
{
    int calls = 0;
    var result = await NativeAppAuthorization.ExecuteAsync(new JsonObject(), _ => { calls++; return Task.FromResult(new JsonObject { ["ok"] = false, ["error"] = "failed" }); });
    Assert(calls == 1 && !result["ok"]!.GetValue<bool>());
});
await Test("unaccepted repeated app challenge fails without looping", () => Throws<InvalidOperationException>(() =>
    NativeAppAuthorization.ExecuteAsync(new JsonObject(), _ => Task.FromResult(new JsonObject { ["ok"] = false, ["approvalRequest"] = new JsonObject { ["app"] = "test" } }))));
await Test("app authorization does not carry over to another request", async () =>
{
    await NativeAppAuthorization.ExecuteAsync(new JsonObject(), sent => Task.FromResult(sent["x-oai-cua-approved-app"] is null
        ? new JsonObject { ["ok"] = false, ["approvalRequest"] = new JsonObject { ["app"] = "first" } }
        : new JsonObject { ["ok"] = true }));
    await NativeAppAuthorization.ExecuteAsync(new JsonObject(), sent => { Assert(sent["x-oai-cua-approved-app"] is null); return Task.FromResult(new JsonObject { ["ok"] = true }); });
});
await Test("elevation broker refuses an unelevated main-session invocation before IPC", () => Throws<UnauthorizedAccessException>(() =>
    ElevationBroker.RunAsync(["elevation-broker", "unused", "999", Environment.ProcessId.ToString(), Native.CurrentSession.ToString(), "0", "test"])));
await Test("elevation cannot be requested from the child session", () => Throws<UnauthorizedAccessException>(() => Sync(() =>
    ElevationBroker.StartInfo("unused", new Binding(Native.CurrentSession, "test")))));
await Test("pipe ACL grants the local user and denies network logons", () => Sync(() =>
{
    var rules = AuthenticatedPipe.Security().GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier))
        .Cast<System.IO.Pipes.PipeAccessRule>().ToArray();
    Assert(rules.Any(rule => rule.IdentityReference.Value == Native.UserSid && rule.AccessControlType == System.Security.AccessControl.AccessControlType.Allow));
    Assert(rules.Any(rule => rule.IdentityReference.Value == "S-1-5-2" && rule.AccessControlType == System.Security.AccessControl.AccessControlType.Deny));
}));
await Test("every native method has a named passthrough tool", () => Sync(() =>
{
    foreach (var method in ComputerMethods.Allowed)
    {
        var tool = NativeToolCatalog.Tool(method);
        Assert(tool["name"]!.GetValue<string>() == method);
        Assert(tool["inputSchema"]!["required"]!.AsArray().Any(x => x!.GetValue<string>() == "sessionId"));
    }
    Assert(ComputerMethods.Allowed.Contains("start_audio_recording") && ComputerMethods.Allowed.Contains("stop_audio_recording"));
}));
await Test("passthrough removes only routing fields and preserves native payload", () => Sync(() =>
{
    var input = new JsonObject { ["sessionId"] = 8, ["generation"] = "generation", ["text"] = "测试", ["window"] = new JsonObject { ["id"] = 42, ["app"] = "test" } };
    var routed = NativeToolCatalog.ForwardArguments("type_text", input);
    Assert(routed["params"]!["text"]!.GetValue<string>() == "测试");
    Assert(routed["params"]!["window"]!["id"]!.GetValue<int>() == 42 && routed["params"]!["sessionId"] is null);
    Assert(input["text"]!.GetValue<string>() == "测试");
}));
await Test("admin launch validates path and structured arguments without launching", () => Sync(() =>
{
    var spec = ProcessLaunchSpec.FromArguments(new JsonObject { ["executablePath"] = Environment.ProcessPath, ["arguments"] = new JsonArray("space here", "quote\"here", "") });
    Assert(spec.Image.Sha256 == identity.Sha256 && spec.Arguments.Length == 3);
}));
await Test("admin launch rejects relative paths before any elevation prompt", () => Throws<ArgumentException>(() => Sync(() =>
    ProcessLaunchSpec.FromArguments(new JsonObject { ["executablePath"] = "notepad.exe" }))));
await Test("admin launch rejects a shell command string as arguments", () => Throws<ArgumentException>(() => Sync(() =>
    ProcessLaunchSpec.FromArguments(new JsonObject { ["executablePath"] = Environment.ProcessPath, ["arguments"] = "one && two" }))));
await Test("admin launch rejects wrong child session before invoking Windows", () => Throws<UnauthorizedAccessException>(() => Sync(() =>
    new ProcessLaunchSpec(identity, [], AppContext.BaseDirectory).LaunchAsAdmin(Native.CurrentSession + 100))));
await Test("desktop ownership is exclusive and released on dispose", () => Sync(() =>
{
    var path = Path.Combine(Path.GetTempPath(), "bcu-test-" + Guid.NewGuid().ToString("N"), "desktop.lock");
    using (var owner = DesktopLease.Acquire(path))
    {
        bool rejected = false;
        try { using var competing = DesktopLease.Acquire(path); }
        catch (InvalidOperationException ex) { rejected = ex.Message.Contains("desktop host already holds"); }
        Assert(rejected);
    }
    using (var nextOwner = DesktopLease.Acquire(path)) { }
    File.Delete(path); Directory.Delete(Path.GetDirectoryName(path)!);
}));
await Test("View disables native RDP input; Take control enables it; hide releases it", () => Sta(() =>
{
    using var viewer = new RdpWindow(new ViewerPreferences { Persist = false }); viewer.Show();
    viewer.SetHumanControl(false);
    Assert(!viewer.HumanControl && !Native.IsWindowEnabled(viewer.Rdp.Handle) && !viewer.Rdp.TabStop);
    viewer.SetHumanControl(true);
    Assert(viewer.HumanControl && Native.IsWindowEnabled(viewer.Rdp.Handle));
    viewer.SetViewer(false);
    Assert(!viewer.HumanControl && !Native.IsWindowEnabled(viewer.Rdp.Handle));
    viewer.CloseHost();
}));
await Test("View PiP takes two explicit clicks and never enables input on reveal", () => Sta(() =>
{
    using var viewer = new RdpWindow(new ViewerPreferences { Persist = false }); viewer.Show();
    viewer.SetHumanControl(false); viewer.SetViewer(true);
    int requests = 0;
    viewer.ControlRequested += requested => { if (requested) { requests++; viewer.SetHumanControl(true); } };
    viewer.RequestTakeover(); Assert(requests == 0);
    viewer.RevealControls();
    Assert(viewer.ViewMode == "pip" && requests == 0 && !Native.IsWindowEnabled(viewer.Rdp.Handle));
    viewer.RequestTakeover();
    Assert(requests == 1 && viewer.ViewMode == "expanded" && Native.IsWindowEnabled(viewer.Rdp.Handle));
    viewer.CloseHost();
}));
await Test("View collapse resumes by default and manual mode keeps input paused", () => Sta(() =>
{
    using var viewer = new RdpWindow(new ViewerPreferences { Persist = false }); viewer.Show(); viewer.SetViewer(true);
    int releases = 0; viewer.ControlRequested += requested => { if (!requested) releases++; else viewer.SetHumanControl(true); };
    viewer.SetHumanControl(true); viewer.Collapse();
    Assert(releases == 1 && !viewer.HumanControl && viewer.ViewMode == "pip" && !Native.IsWindowEnabled(viewer.Rdp.Handle));
    viewer.ResumeMode = ResumeMode.Manual; viewer.SetHumanControl(true); viewer.Collapse();
    Assert(releases == 1 && viewer.HumanControl && !Native.IsWindowEnabled(viewer.Rdp.Handle));
    Assert(viewer.ControlButtonText == "Taken control" && !viewer.ControlButtonEnabled);
    viewer.Expand();
    Assert(viewer.HumanControl && viewer.ViewMode == "expanded" && Native.IsWindowEnabled(viewer.Rdp.Handle));
    viewer.ReturnToAgent(); Assert(releases == 2 && !viewer.HumanControl && viewer.ControlButtonText == "Take over" && viewer.ControlButtonEnabled);
    viewer.CloseHost();
}));
await Test("View collapsing cancels a pending takeover before input is granted", () => Sta(() =>
{
    using var viewer = new RdpWindow(new ViewerPreferences { Persist = false }); viewer.Show(); viewer.SetViewer(true);
    var requests = new List<bool>(); viewer.ControlRequested += requested => requests.Add(requested);
    viewer.SetHumanControl(false); viewer.RevealControls(); viewer.RequestTakeover(); viewer.Collapse();
    Assert(requests.SequenceEqual(new[] { true, false }) && !Native.IsWindowEnabled(viewer.Rdp.Handle));
    viewer.CloseHost();
}));
await Test("View click-out preference ignores focus changes and owned dialogs", () => Sta(() =>
{
    using var viewer = new RdpWindow(new ViewerPreferences { Persist = false }); viewer.Show(); viewer.SetViewer(true);
    viewer.ResumeMode = ResumeMode.OnClickOutside; viewer.SetHumanControl(true);
    int releases = 0; viewer.ControlRequested += requested => { if (!requested) releases++; };
    viewer.ObservePointer(false, true, true); Assert(viewer.HumanControl);
    viewer.ObservePointer(true, true, false); Assert(viewer.HumanControl); // menu or secure desktop
    viewer.ObservePointer(false, true, true); viewer.ObservePointer(true, true, true);
    Assert(!viewer.HumanControl && releases == 1 && viewer.ViewMode == "pip");
    viewer.ResumeMode = ResumeMode.Manual; viewer.SetHumanControl(true);
    viewer.ObservePointer(false, true, true); viewer.ObservePointer(true, true, true);
    Assert(viewer.HumanControl && releases == 1);
    viewer.ReturnToAgent(); viewer.CloseHost();
}));
await Test("View minimizing the large window collapses and resumes without minimizing RDP", () => Sta(() =>
{
    using var viewer = new RdpWindow(new ViewerPreferences { Persist = false }); viewer.Show(); viewer.SetViewer(true);
    viewer.SetHumanControl(true); Native.SendMessage(viewer.Handle, 0x112, 0xf020, 0);
    Assert(!viewer.HumanControl && viewer.ViewMode == "pip" && viewer.WindowState == System.Windows.Forms.FormWindowState.Normal);
    viewer.CloseHost();
}));
await Test("View read-only expansion does not request or enable human control", () => Sta(() =>
{
    using var viewer = new RdpWindow(new ViewerPreferences { Persist = false }); viewer.Show();
    viewer.SetHumanControl(false); viewer.SetViewer(true);
    viewer.ControlRequested += _ => throw new Exception("Unexpected control request");
    viewer.Expand();
    Assert(viewer.ViewMode == "expanded" && viewer.TopMost && !viewer.HumanControl && !Native.IsWindowEnabled(viewer.Rdp.Handle));
    viewer.CloseHost();
}));
await Test("View hovering reveals PiP chrome without resizing the remote preview", () => Sta(() =>
{
    using var viewer = new RdpWindow(new ViewerPreferences { Persist = false }); viewer.Show(); viewer.SetViewer(true);
    var preview = viewer.Rdp.Bounds;
    viewer.UpdateHover(viewer.PointToScreen(new System.Drawing.Point(40, 15)));
    Assert(viewer.Rdp.Bounds == preview && viewer.ViewMode == "pip" && !viewer.HumanControl);
    viewer.UpdateHover(new System.Drawing.Point(-10000, -10000));
    Assert(viewer.Rdp.Bounds == preview);
    viewer.CloseHost();
}));
await Test("View read-only expansion collapses on click-out without changing control", () => Sta(() =>
{
    using var viewer = new RdpWindow(new ViewerPreferences { Persist = false }); viewer.Show(); viewer.SetViewer(true);
    viewer.Expand(); viewer.ObservePointer(true, true, true);
    Assert(viewer.ViewMode == "pip" && !viewer.HumanControl && viewer.TopMost);
    viewer.CloseHost();
}));
await Test("viewer geometry fits the desktop without grey padding or stretching", () => Sync(() =>
{
    var desktop = new System.Drawing.Size(1920, 1080);
    foreach (var area in new[] { new System.Drawing.Rectangle(0, 0, 2560, 1400), new System.Drawing.Rectangle(-1280, 0, 1280, 984) })
    {
        var fitted = ViewerGeometry.Fit(desktop, area, 38, 1);
        Assert(area.Contains(fitted) && fitted.Width <= 1922 && fitted.Height <= 1120);
        Assert(Math.Abs((fitted.Width - 2d) / (fitted.Height - 40d) - 16d / 9) < .002);
        var resized = ViewerGeometry.Resize(fitted, desktop, 38, 3, area.Size);
        Assert(Math.Abs((resized.Width - 2d) / (resized.Height - 40d) - 16d / 9) < .002);
    }
}));
await Test("View hidden PiP leaves no taskbar ghost and restores as an app window", () => Sta(() =>
{
    using var viewer = new RdpWindow(new ViewerPreferences { Persist = false }); viewer.Show(); viewer.SetViewer(true);
    Assert(viewer.ShowInTaskbar && (Native.WindowStyle(viewer.Handle, -20) & 0x40000) != 0);
    viewer.SetViewer(false);
    Assert(!viewer.ShowInTaskbar && (Native.WindowStyle(viewer.Handle, -20) & 0x40000) == 0);
    Assert((Native.WindowStyle(viewer.Handle, -20) & 0x80) != 0);
    viewer.SetViewer(true);
    Assert(viewer.ShowInTaskbar && (Native.WindowStyle(viewer.Handle, -20) & 0x80) == 0);
    viewer.CloseHost();
}));
await Test("View expanded header reserves space instead of covering the desktop", () => Sta(() =>
{
    using var viewer = new RdpWindow(new ViewerPreferences { Persist = false }); viewer.Show(); viewer.SetViewer(true);
    Assert(viewer.Rdp.Parent!.Top == 1);
    viewer.Expand();
    var preview = viewer.Rdp.Parent!;
    Assert(preview.Top == 38 * viewer.DeviceDpi / 96 + 1);
    Assert(preview.Bottom == viewer.ClientSize.Height - 1);
    Assert(Math.Abs(preview.Width / (double)preview.Height - 16d / 9) < .002);
    viewer.Collapse(); Assert(viewer.Rdp.Parent!.Top == 1);
    viewer.CloseHost();
}));
await Test("View clicking again hides the action buttons without taking over", () => Sta(() =>
{
    using var viewer = new RdpWindow(new ViewerPreferences { Persist = false }); viewer.Show(); viewer.SetViewer(true);
    viewer.ToggleControls(); Assert(viewer.ControlsRevealed && !viewer.HumanControl);
    viewer.ToggleControls(); Assert(!viewer.ControlsRevealed && !viewer.HumanControl);
    viewer.Expand(); Assert(viewer.ControlsRevealed);
    viewer.ToggleControls(); Assert(!viewer.ControlsRevealed && viewer.ViewMode == "expanded");
    viewer.CloseHost();
}));
await Test("View auto collapse can be disabled independently of automatic resume", () => Sta(() =>
{
    using var viewer = new RdpWindow(new ViewerPreferences { Persist = false }); viewer.Show(); viewer.SetViewer(true);
    viewer.CollapseOnClickOutside = false; viewer.Expand(); viewer.ObservePointer(true, true, true);
    Assert(viewer.ViewMode == "expanded" && !viewer.HumanControl);
    viewer.ResumeMode = ResumeMode.OnClickOutside; viewer.SetHumanControl(true);
    viewer.ObservePointer(false, true, true); viewer.ObservePointer(true, true, true);
    Assert(viewer.ViewMode == "expanded" && !viewer.HumanControl);
    viewer.CloseHost();
}));
await Test("agent handoff invalidates old input and supports taking control back", async () =>
{
    var control = new DesktopControl();
    JsonObject Args(Binding b) => new() { ["sessionId"] = b.SessionId, ["generation"] = b.Generation };
    var a = control.Attach("a", 42, true);
    control.Validate("a", Args(a), true);
    var observer = control.Attach("b", 42, false);
    control.Validate("b", Args(observer), false);
    await Throws<InvalidOperationException>(() => Sync(() => control.Validate("b", Args(observer), true)));
    var b = control.Attach("b", 42, true);
    control.Validate("b", Args(b), true);
    await Throws<InvalidOperationException>(() => Sync(() => control.Validate("a", Args(a), true)));
    control.Validate("a", Args(control.BindingFor("a")!), false);
    var nextA = control.Attach("a", 42, true);
    Assert(nextA.Generation != a.Generation);
    control.Validate("a", Args(nextA), true);
    await Throws<InvalidOperationException>(() => Sync(() => control.Validate("b", Args(b), true)));
});
await Test("detaching another agent preserves controller; reset rejects every old binding", async () =>
{
    var control = new DesktopControl(); var a = control.Attach("a", 42, true);
    control.Attach("b", 42, false); control.Detach("b"); Assert(control.Controller == "a");
    control.Reset(); Assert(control.Controller is null);
    await Throws<InvalidOperationException>(() => Sync(() => control.Validate("a", new() { ["sessionId"] = 42, ["generation"] = a.Generation }, true)));
});
await Test("wrapper forwards installed helper protocol shape and Unicode", async () =>
{
    using var helper = new HelperProcess(identity, requireElevation: false);
    var result = await helper.RequestAsync("echo", new JsonObject { ["text"] = "你好" }, null, default);
    Assert(result["ok"]!.GetValue<bool>() && result["result"]!["text"]!.GetValue<string>() == "你好");
});
await Test("raw transport preserves the challenge for request-level authorization", async () =>
{
    using var helper = new HelperProcess(identity, requireElevation: false);
    var result = await helper.RequestAsync("approval", new JsonObject(), null, default);
    Assert(!result["ok"]!.GetValue<bool>() && result["approvalRequest"]!["app"]!.GetValue<string>() == "test-app");
});
await Test("mismatched helper response kills connection, never retries", async () =>
{
    using var helper = new HelperProcess(identity, requireElevation: false);
    await Throws<InvalidDataException>(() => helper.RequestAsync("bad-id", new JsonObject(), null, default));
    await Throws<InvalidOperationException>(() => helper.RequestAsync("echo", new JsonObject(), null, default));
});
await Test("cancelled helper request terminates helper", async () =>
{
    using var helper = new HelperProcess(identity, requireElevation: false);
    using var process = Process.GetProcessById(helper.Pid);
    using var cancel = new CancellationTokenSource(200);
    await Throws<OperationCanceledException>(() => helper.RequestAsync("hang", new JsonObject(), null, cancel.Token));
    Assert(process.WaitForExit(5000));
});
await Test("kernel pipe session identity rejects a different session", async () =>
{
    var name = "bcu-test-" + Guid.NewGuid().ToString("N");
    using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
    await Task.WhenAll(server.WaitForConnectionAsync(), client.ConnectAsync());
    await Task.WhenAll(Wire.WriteAsync(client, new { hello = true }, default), Wire.ReadAsync(server, default));
    await Throws<UnauthorizedAccessException>(() => Sync(() => Native.VerifyClient(server, Native.CurrentSession + 100, DateTime.UtcNow.AddMinutes(-1))));
});
await Test("identity-only pipe authenticates without impersonating client privileges", async () =>
{
    var name = "bcu-test-" + Guid.NewGuid().ToString("N");
    using var server = AuthenticatedPipe.CreateServer(name);
    using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous,
        System.Security.Principal.TokenImpersonationLevel.Identification);
    await Task.WhenAll(server.WaitForConnectionAsync(), client.ConnectAsync());
    await Task.WhenAll(Wire.WriteAsync(client, new { hello = true }, default), Wire.ReadAsync(server, default));
    using var verified = Native.VerifyClient(server, Native.CurrentSession, Process.GetCurrentProcess().StartTime.ToUniversalTime().AddSeconds(-1));
    Assert(verified.Id == Environment.ProcessId);
});
foreach (var mismatch in new[] { "session", "elevation", "image" })
{
    await Test("suspended application cannot execute on " + mismatch + " mismatch", async () =>
    {
        var marker = Path.Combine(Path.GetTempPath(), "bcu-marker-" + Guid.NewGuid().ToString("N"));
        int pid;
        using (var child = new SuspendedProcess(Environment.ProcessPath!, ["--launch-marker", marker], AppContext.BaseDirectory))
        {
            pid = child.Pid;
            await Throws<InvalidOperationException>(() => Sync(() => child.VerifyAndResume(
                Native.CurrentSession + (mismatch == "session" ? 100 : 0),
                mismatch == "elevation" ? !Native.Elevated : Native.Elevated,
                mismatch == "image" ? "wrong.exe" : Environment.ProcessPath!)));
        }
        Assert(!File.Exists(marker));
        try { using var child = Process.GetProcessById(pid); Assert(child.HasExited); }
        catch (ArgumentException) { }
    });
}
await Test("verified suspended application runs with exact structured arguments", async () =>
{
    var marker = Path.Combine(Path.GetTempPath(), "bcu-marker-" + Guid.NewGuid().ToString("N"));
    try
    {
        using var child = new SuspendedProcess(Environment.ProcessPath!, ["--launch-marker", marker, "space here", "quote\"here", "trailing\\"], AppContext.BaseDirectory);
        using var process = Process.GetProcessById(child.Pid);
        Assert(!File.Exists(marker));
        child.VerifyAndResume(Native.CurrentSession, Native.Elevated, Environment.ProcessPath!);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert(File.ReadAllText(marker) == "space here|quote\"here|trailing\\");
    }
    finally { File.Delete(marker); }
});
await Test("disconnected recovery logs off only the observed child and releases ownership", async () =>
{
    int? child = 42;
    var path = Path.Combine(Path.GetTempPath(), "bcu-recovery-" + Guid.NewGuid().ToString("N"), "lock");
    var recovery = new DisconnectedSessionRecovery(1, () => child, _ => 4, () => DesktopLease.Acquire(path), id => { Assert(id == 42); child = null; });
    await recovery.LogoffAsync(42, default);
    Assert(child is null);
    using (DesktopLease.Acquire(path)) { }
    File.Delete(path); Directory.Delete(Path.GetDirectoryName(path)!);
});
foreach (var scenario in new[] { "busy", "connected", "changed", "parent", "zero" })
    await Test("recovery refuses " + scenario + " session without logging off", async () =>
    {
        var path = Path.Combine(Path.GetTempPath(), "bcu-recovery-" + Guid.NewGuid().ToString("N"), "lock");
        using var owner = scenario == "busy" ? DesktopLease.Acquire(path) : null;
        var invoked = false;
        var recovery = new DisconnectedSessionRecovery(1, () => scenario == "changed" ? 43 : 42,
            _ => scenario == "connected" ? 0 : 4, () => DesktopLease.Acquire(path), _ => invoked = true);
        await Throws<InvalidOperationException>(() => recovery.LogoffAsync(scenario == "parent" ? 1 : scenario == "zero" ? 0 : 42, default));
        Assert(!invoked);
        owner?.Dispose(); File.Delete(path); Directory.Delete(Path.GetDirectoryName(path)!);
    });
await Test("UAC cancellation selects user mode without a second elevation request", async () =>
{
    var adminCalls = 0; var userCalls = 0;
    var result = await SessionWorkerStartup.StartAsync("admin",
        () => SessionWorkerStartup.RequestUacAsync<int>(() => { adminCalls++; throw new System.ComponentModel.Win32Exception(1223); }),
        () => { userCalls++; return Task.FromResult(7); });
    Assert(result.Mode == "user" && result.UacDeclined && result.Worker == 7 && adminCalls == 1 && userCalls == 1);
});
await Test("access denied is not treated as declined UAC", async () =>
{
    bool userCalled = false;
    await Throws<System.ComponentModel.Win32Exception>(() => SessionWorkerStartup.StartAsync("admin",
        () => SessionWorkerStartup.RequestUacAsync<int>(() => throw new System.ComponentModel.Win32Exception(5)),
        () => { userCalled = true; return Task.FromResult(0); }));
    Assert(!userCalled);
});
await Test("explicit user mode never invokes UAC", async () =>
{
    var result = await SessionWorkerStartup.StartAsync("user", () => throw new Exception("Unexpected UAC"), () => Task.FromResult(3));
    Assert(result.Mode == "user" && !result.UacDeclined && result.Worker == 3);
});
await Test("accepted admin startup retains elevated mode", async () =>
{
    var result = await SessionWorkerStartup.StartAsync("admin", () => Task.FromResult(4), () => throw new Exception("Unexpected fallback"));
    Assert(result.Mode == "admin" && !result.UacDeclined && result.Worker == 4);
});
await Test("default helper startup refuses an unelevated caller", () => Throws<UnauthorizedAccessException>(() => Sync(() =>
{
    using var helper = new HelperProcess(identity);
})));
Console.WriteLine($"{count} tests passed; {skipped} interactive checks skipped.");

using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BetterComputerUse;

internal static class Worker
{
    internal static async Task RunAsync(string[] args, bool elevated)
    {
        if (args.Length != 7) throw new ArgumentException("Invalid internal worker arguments.");
        var pipeName = args[1];
        var expectedSession = int.Parse(args[2]);
        var parentPid = int.Parse(args[3]);
        var parentSession = int.Parse(args[4]);
        var parentStart = long.Parse(args[5]);
        var generation = args[6];
        if (Native.CurrentSession != expectedSession || expectedSession == parentSession || expectedSession == 0)
            throw new UnauthorizedAccessException("Worker must run in the specified child session, never the parent desktop.");
        if (Native.Elevated != elevated) throw new UnauthorizedAccessException("Worker elevation does not match its role.");
        if (args[0] is not ("session-worker" or "elevated-worker" or "session-user-worker") || (args[0] == "session-user-worker") == elevated)
            throw new UnauthorizedAccessException("Worker must match the selected admin or user-space session mode.");
        var persistent = args[0] is "session-worker" or "session-user-worker";

        using var lifetime = new CancellationTokenSource();
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(20000, lifetime.Token);
        Native.VerifyServer(pipe, parentPid, parentSession, parentStart);
        if (elevated) ProcessQueryAccess.Grant(Environment.ProcessId);
        var binding = new Binding(expectedSession, generation);
        await Wire.WriteAsync(pipe, new { kind = "hello", sessionId = expectedSession, generation, elevated,
            pid = Environment.ProcessId }, lifetime.Token);
        var config = await Wire.ReadAsync(pipe, lifetime.Token);
        binding.Validate(config);
        var identity = config["helper"]!.Deserialize<HelperIdentity>(Wire.Json)!;
        HelperProcess createdHelper;
        try { createdHelper = new HelperProcess(identity, requireElevation: elevated); }
        catch (Exception ex)
        {
            await Wire.WriteAsync(pipe, new { kind = "fault", error = ex.GetBaseException().Message }, lifetime.Token);
            return;
        }
        using var helper = createdHelper;
        if (elevated) ProcessQueryAccess.Grant(helper.Pid);
        await Wire.WriteAsync(pipe, new { kind = "ready", helperPid = helper.Pid }, lifetime.Token);
        // Losing the parent or transport kills only our helper, never applications the user opened.
        _ = Task.Run(async () =>
        {
            try { using var parent = Process.GetProcessById(parentPid); await parent.WaitForExitAsync(lifetime.Token); lifetime.Cancel(); pipe.Dispose(); }
            catch (Exception ex) when (ex is ArgumentException or OperationCanceledException or ObjectDisposedException) { }
        });
        long lastSequence = 0;
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var request = await Wire.ReadAsync(pipe, lifetime.Token);
                binding.Validate(request);
                var sequence = request["sequence"]!.GetValue<long>();
                if (sequence != lastSequence + 1) throw new UnauthorizedAccessException("Replayed or out-of-order IPC request.");
                lastSequence = sequence;
                var operation = request.RequiredString("operation");
                JsonObject response;
                if (operation == "shutdown") return;
                if (operation == "launch_process_as_admin" && !persistent)
                {
                    var spec = request["launch"]!.Deserialize<ProcessLaunchSpec>(Wire.Json)!;
                    response = spec.LaunchAsAdmin(expectedSession);
                }
                else if (operation == "computer")
                {
                    var method = request.RequiredString("method");
                    var parameters = request["params"] as JsonObject ?? new JsonObject();
                    ComputerMethods.Validate(method, parameters);
                    response = await helper.RequestAsync(method, parameters, request["meta"] as JsonObject, lifetime.Token);
                }
                else throw new ArgumentException("Unsupported worker operation.");
                await Wire.WriteAsync(pipe, new { sequence, response }, lifetime.Token);
                // The session worker persists for computer use; explicit admin launches
                // still use a separate UAC-backed one-shot worker.
                if (!persistent && response["approvalRequest"] is null) return;
            }
        }
        finally { lifetime.Cancel(); }
    }
}

internal static class Launcher
{
    internal static string Arguments(IEnumerable<string> args) => string.Join(" ", args.Select(Quote));
    internal static string Quote(string value)
    {
        // CommandLineToArgvW escaping: double trailing backslashes and those preceding quotes.
        var output = new System.Text.StringBuilder("\"");
        int slashes = 0;
        foreach (var ch in value)
        {
            if (ch == '\\') { slashes++; continue; }
            output.Append('\\', ch == '"' ? slashes * 2 + 1 : slashes);
            output.Append(ch); slashes = 0;
        }
        return output.Append('\\', slashes * 2).Append('"').ToString();
    }

    internal static string[] WorkerArgs(string role, string pipe, Binding binding)
    {
        using var parent = Process.GetCurrentProcess();
        return [role, pipe, binding.SessionId.ToString(), parent.Id.ToString(), parent.SessionId.ToString(),
            parent.StartTime.ToUniversalTime().Ticks.ToString(), binding.Generation];
    }

    internal static void LaunchStandard(string pipe, Binding binding)
        => LaunchTask(WorkerArgs("session-user-worker", pipe, binding), binding, false);

    internal static void LaunchTask(string[] arguments, Binding binding, bool elevated)
    {
        if (Native.ChildSession() != binding.SessionId) throw new InvalidOperationException("Child session changed before launch.");
        if (elevated && !Native.Elevated) throw new UnauthorizedAccessException("An elevated main-session broker is required.");
        var objects = new List<object>();
        string taskName = "BetterComputerUse-" + Guid.NewGuid().ToString("N");
        dynamic? folder = null;
        bool registered = false;
        object Track(object obj) { objects.Add(obj); return obj; }
        try
        {
            dynamic service = Track(Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!);
            service.Connect();
            folder = Track(service.GetFolder("\\"));
            dynamic definition = Track(service.NewTask(0));
            dynamic principal = Track(definition.Principal);
            using var user = WindowsIdentity.GetCurrent();
            principal.UserId = user.Name; principal.LogonType = 3; principal.RunLevel = elevated ? 1 : 0;
            dynamic settings = Track(definition.Settings);
            settings.Enabled = true; settings.Hidden = true; settings.AllowDemandStart = true;
            settings.DisallowStartIfOnBatteries = false; settings.StopIfGoingOnBatteries = false;
            settings.ExecutionTimeLimit = "PT0S";
            dynamic actions = Track(definition.Actions);
            dynamic action = Track(actions.Create(0));
            action.Path = Environment.ProcessPath!;
            action.Arguments = Arguments(arguments);
            action.WorkingDirectory = AppContext.BaseDirectory;
            dynamic task = Track(folder.RegisterTaskDefinition(taskName, definition, 2, user.Name, null, 3, null));
            registered = true;
            Track(task.RunEx(null, 4, binding.SessionId, null));
        }
        finally
        {
            if (registered)
            {
                try { folder!.DeleteTask(taskName, 0); }
                catch (Exception ex) { Console.Error.WriteLine($"Temporary task cleanup failed ({taskName}): {ex.Message}"); }
            }
            foreach (var obj in objects.AsEnumerable().Reverse())
                if (System.Runtime.InteropServices.Marshal.IsComObject(obj)) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(obj);
        }
    }
}

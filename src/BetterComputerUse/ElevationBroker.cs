using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json.Nodes;

namespace BetterComputerUse;

internal sealed class UacDeclinedException() : Exception("Windows UAC was declined.");

internal static class ElevationBroker
{
    internal static ProcessStartInfo StartInfo(string pipe, Binding binding)
    {
        if (Native.CurrentSession == 0 || binding.SessionId == Native.CurrentSession || Native.ChildSession() != binding.SessionId)
            throw new UnauthorizedAccessException("Elevation must be requested from the owning main session.");
        return new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = Launcher.Arguments(Launcher.WorkerArgs("elevation-broker", pipe, binding)) };
    }

    internal static async Task RunAsync(string[] args)
    {
        if (args.Length != 7) throw new ArgumentException("Invalid broker bootstrap arguments.");
        var pipeName = args[1];
        var binding = new Binding(int.Parse(args[2]), args[6]);
        var parentPid = int.Parse(args[3]); var parentSession = int.Parse(args[4]); var parentStart = long.Parse(args[5]);
        if (!Native.Elevated || Native.CurrentSession != parentSession || parentSession == 0 || binding.SessionId == parentSession)
            throw new UnauthorizedAccessException("The elevation broker must be elevated in the main session, never the child session.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(20000, timeout.Token);
        Native.VerifyServer(pipe, parentPid, parentSession, parentStart);
        await Wire.WriteAsync(pipe, new { kind = "hello", binding.SessionId, binding.Generation, pid = Environment.ProcessId }, timeout.Token);
        var request = await Wire.ReadAsync(pipe, timeout.Token);
        binding.Validate(request);
        if (request.RequiredString("operation") != "spawn_worker" || Native.ChildSession() != binding.SessionId)
            throw new UnauthorizedAccessException("Invalid elevated worker request or changed child session.");
        var workerPipe = request.RequiredString("workerPipe");
        if (!workerPipe.StartsWith("bcu-", StringComparison.Ordinal) || workerPipe.Length > 80)
            throw new ArgumentException("Invalid worker pipe name.");
        JsonObject response;
        var workerRole = request["workerRole"]?.GetValue<string>() ?? "elevated-worker";
        if (workerRole is not ("elevated-worker" or "session-worker")) throw new ArgumentException("Invalid elevated worker role.");
        try
        {
            await Task.Run(() => Launcher.LaunchTask([workerRole, workerPipe, binding.SessionId.ToString(), parentPid.ToString(), parentSession.ToString(), parentStart.ToString(), binding.Generation], binding, true)).WaitAsync(timeout.Token);
            response = new JsonObject { ["ok"] = true };
        }
        catch (Exception ex) { response = new JsonObject { ["ok"] = false, ["error"] = "Scheduling elevated child worker: " + ex.GetBaseException().Message }; }
        await Wire.WriteAsync(pipe, response, timeout.Token);
        // One authenticated fixed-executable launch, then exit. No arbitrary broker commands.
    }

    internal static async Task SpawnWorkerAsync(Binding binding, string workerPipe, Func<ProcessStartInfo, Task<Process>> showUac, CancellationToken ct, bool persistent = false)
    {
        var name = "bcu-elevation-" + Guid.NewGuid().ToString("N");
        using var pipe = AuthenticatedPipe.CreateServer(name);
        var earliest = DateTime.UtcNow.AddSeconds(-1);
        string stage = "starting main-session UAC broker";
        try
        {
        using var launched = await SessionWorkerStartup.RequestUacAsync(() => showUac(StartInfo(name, binding))); // Main session only.
        stage = "waiting for main-session UAC broker IPC";
        await pipe.WaitForConnectionAsync(ct);
        var hello = await Wire.ReadAsync(pipe, ct);
        stage = "authenticating main-session UAC broker";
        using var verified = Native.VerifyClient(pipe, Native.CurrentSession, earliest);
        binding.Validate(hello);
        if (verified.Id != launched.Id || !Native.IsProcessElevated(verified.Id) || hello["kind"]?.GetValue<string>() != "hello")
            throw new UnauthorizedAccessException("Unexpected main-session elevation broker.");
        stage = "requesting elevated child worker";
        await Wire.WriteAsync(pipe, new { operation = "spawn_worker", binding.SessionId, binding.Generation, workerPipe,
            workerRole = persistent ? "session-worker" : "elevated-worker" }, ct);
        var result = await Wire.ReadAsync(pipe, ct);
        if (result["ok"]?.GetValue<bool>() != true) throw new InvalidOperationException(result["error"]?.GetValue<string>() ?? "Elevated worker launch failed.");
        }
        catch (UacDeclinedException) { throw; }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Admin launch failed while {stage}: {ex.Message}. No application launch request was sent; do not fall back to the main desktop.");
        }
    }
}

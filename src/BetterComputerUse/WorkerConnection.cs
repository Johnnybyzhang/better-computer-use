using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json.Nodes;

namespace BetterComputerUse;

internal sealed class WorkerConnection : IDisposable
{
    private readonly NamedPipeServerStream pipe;
    private readonly Binding binding;
    private readonly DateTime earliestStart = DateTime.UtcNow.AddSeconds(-1);
    private Process? worker;
    private long sequence;
    internal int? WorkerPid => worker?.Id;
    internal int? HelperPid { get; private set; }
    internal bool IsAlive
    {
        get
        {
            try { return pipe.IsConnected && worker is { HasExited: false }; }
            catch (InvalidOperationException) { return false; }
        }
    }
    internal string PipeName { get; } = "bcu-" + Guid.NewGuid().ToString("N");

    internal WorkerConnection(Binding binding)
    {
        this.binding = binding;
        pipe = AuthenticatedPipe.CreateServer(PipeName);
    }

    internal async Task AcceptAsync(HelperIdentity helper, bool elevated, CancellationToken ct, int? expectedPid = null)
    {
        await pipe.WaitForConnectionAsync(ct);
        // Impersonation requires the client to have written at least one message.
        var hello = await Wire.ReadAsync(pipe, ct);
        worker = Native.VerifyClient(pipe, binding.SessionId, earliestStart);
        binding.Validate(hello);
        if (hello["kind"]?.GetValue<string>() != "hello" || hello["pid"]?.GetValue<int>() != worker.Id ||
            hello["elevated"]?.GetValue<bool>() != elevated || Native.IsProcessElevated(worker.Id) != elevated || expectedPid is not null && worker.Id != expectedPid)
            throw new UnauthorizedAccessException("Worker handshake mismatch.");
        await Wire.WriteAsync(pipe, new { binding.SessionId, binding.Generation, helper }, ct);
        var ready = await Wire.ReadAsync(pipe, ct);
        if (ready["kind"]?.GetValue<string>() != "ready") throw new InvalidDataException(ready["error"]?.GetValue<string>() ?? "Worker not ready.");
        HelperPid = ready["helperPid"]!.GetValue<int>();
        using var process = Process.GetProcessById(HelperPid.Value);
        if (process.SessionId != binding.SessionId || Native.IsProcessElevated(process.Id) != elevated ||
            !string.Equals(Native.ProcessImage(process.Id), helper.Path, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Helper session, image or elevation does not match.");
    }

    internal async Task<JsonObject> SendAsync(JsonObject request, CancellationToken ct)
    {
        if (!IsAlive) throw new IOException("Session worker is disconnected.");
        var seq = ++sequence;
        request = (JsonObject)request.DeepClone();
        request["sessionId"] = binding.SessionId; request["generation"] = binding.Generation; request["sequence"] = seq;
        try
        {
            await Wire.WriteAsync(pipe, request, ct);
            var response = await Wire.ReadAsync(pipe, ct);
            if (response["sequence"]?.GetValue<long>() != seq) throw new InvalidDataException("IPC response sequence mismatch.");
            return response["response"] as JsonObject ?? throw new InvalidDataException("Missing worker response.");
        }
        catch { Dispose(); throw; } // An ambiguous result must never be replayed on another worker.
    }

    public void Dispose()
    {
        pipe.Dispose();
        // Closing the pipe makes the worker dispose its helper. Its parent-PID watchdog covers manager crashes.
        Interlocked.Exchange(ref worker, null)?.Dispose();
    }
}

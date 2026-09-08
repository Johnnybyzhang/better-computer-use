using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace BetterComputerUse;

public sealed record HelperIdentity(string Path, string Sha256)
{
    public static HelperIdentity? FindInstalled()
    {
        var root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "runtimes", "cua_node");
        if (!Directory.Exists(root)) return null;
        var files = Directory.EnumerateDirectories(root)
            .Select(directory => new FileInfo(System.IO.Path.Combine(directory, "bin", "node_modules", "@oai", "sky", "bin", "windows", "codex-computer-use.exe")))
            .Where(file => file.Exists).OrderByDescending(file => file.LastWriteTimeUtc);
        var selected = files.FirstOrDefault();
        return selected is null ? null : Load(selected.FullName);
    }

    public static HelperIdentity Load(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        if (!File.Exists(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("Configure the installed codex-computer-use.exe path.", path);
        using var file = File.OpenRead(path);
        return new(path, Convert.ToHexString(SHA256.HashData(file)));
    }
    public void Verify()
    {
        if (Load(Path).Sha256 != Sha256) throw new InvalidOperationException("Computer Use executable changed; restart the manager.");
    }
}

internal sealed class HelperProcess : IDisposable
{
    private readonly Process process;
    // Deny writes/deletes to the image while this wrapper is using it.
    private readonly FileStream imageLock;
    private readonly Queue<string> diagnostics = new();
    private int nextId;
    private bool failed;
    public int Pid => process.Id;

    public HelperProcess(HelperIdentity identity, bool requireElevation = true)
    {
        if (requireElevation && !Native.Elevated) throw new UnauthorizedAccessException("The Computer Use executable must be started by an elevated child worker.");
        imageLock = File.Open(identity.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            identity.Verify();
            var start = new ProcessStartInfo(identity.Path) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = System.IO.Path.GetDirectoryName(identity.Path)! };
            start.ArgumentList.Add("--parent-pid"); start.ArgumentList.Add(Environment.ProcessId.ToString());
            process = Process.Start(start) ?? throw new InvalidOperationException("Computer Use did not start.");
            if (process.SessionId != Native.CurrentSession) { process.Kill(); throw new InvalidOperationException("Helper session mismatch."); }
            _ = DrainErrorsAsync();
        }
        catch { imageLock.Dispose(); throw; }
    }

    private async Task DrainErrorsAsync()
    {
        try
        {
            var buffer = new char[2048];
            int count;
            while ((count = await process.StandardError.ReadAsync(buffer)) > 0)
                lock (diagnostics) { diagnostics.Enqueue(new string(buffer, 0, count)); while (diagnostics.Count > 8) diagnostics.Dequeue(); }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    internal async Task<JsonObject> RequestAsync(string method, JsonObject args, JsonObject? meta, CancellationToken ct)
    {
        if (failed || process.HasExited) throw new InvalidOperationException("Computer Use worker stopped. Restart the session worker explicitly.");
        var id = ++nextId;
        var metadata = meta?.DeepClone() as JsonObject ?? new JsonObject();
        // SessionManager supplies per-request native app authorization; it never persists global permissions.
        metadata["x-oai-cua-request-budget-ms"] = 15000;
        var request = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = args.DeepClone(), ["meta"] = metadata };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await process.StandardInput.WriteLineAsync(request.ToJsonString().AsMemory(), timeout.Token);
            await process.StandardInput.FlushAsync(timeout.Token);
            while (true)
            {
                var line = await Wire.ReadLineAsync(process.StandardOutput, Wire.MaxFrame, timeout.Token)
                    ?? throw new EndOfStreamException("Computer Use exited before responding.");
                var response = JsonNode.Parse(line) as JsonObject ?? throw new InvalidDataException("Invalid helper response.");
                if (response["id"] is null) continue; // Notifications are not responses.
                if (response["id"]!.GetValue<int>() != id) throw new InvalidDataException("Unexpected helper response id.");
                // Preserve ok/result/error/approvalRequest exactly; never retry an action automatically.
                return response;
            }
        }
        catch
        {
            failed = true;
            if (!process.HasExited) process.Kill();
            throw;
        }
    }

    public void Dispose()
    {
        try { if (!process.HasExited) process.Kill(); }
        finally { process.Dispose(); imageLock.Dispose(); }
    }
}

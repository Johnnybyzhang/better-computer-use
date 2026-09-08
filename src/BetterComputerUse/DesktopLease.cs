namespace BetterComputerUse;

// MCP transports are per task. Only desktop creation/ownership is exclusive, not tool discovery.
// An exclusive OS file handle is process-independent and released on crashes; no thread affinity.
internal sealed class DesktopLease : IDisposable
{
    private readonly FileStream handle;
    private DesktopLease(FileStream handle) => this.handle = handle;

    internal static DesktopLease Acquire(string? testPath = null)
    {
        var path = testPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BetterComputerUse", $"desktop-session-{Native.CurrentSession}.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try { return new DesktopLease(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)); }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
        {
            throw new InvalidOperationException("Another task owns the child desktop. Continue in its owning task or stop that session there before creating another. These MCP tools remain available.");
        }
    }

    public void Dispose() => handle.Dispose();
}

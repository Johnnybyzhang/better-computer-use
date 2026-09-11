namespace BetterComputerUse;

// The shared host owns the desktop lease; individual MCP clients share that host.
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
            throw new InvalidOperationException("A desktop host already holds the Windows desktop lease. Connect through the shared host. If an older plugin version is running, disconnect its viewer before switching versions; applications can stay open.");
        }
    }

    public void Dispose() => handle.Dispose();
}

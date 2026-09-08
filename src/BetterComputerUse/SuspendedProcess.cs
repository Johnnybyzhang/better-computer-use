using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace BetterComputerUse;

// Owns only the process created here. Until verification succeeds, it cannot execute.
internal sealed class SuspendedProcess : IDisposable
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        internal int Size;
        internal string? Reserved, Desktop, Title;
        internal int X, Y, Width, Height, XChars, YChars, Fill, Flags;
        internal short Show, ReservedSize;
        internal nint ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { internal nint Process, Thread; internal int Pid, Tid; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string application, StringBuilder command, nint processAttributes,
        nint threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, nint environment,
        string directory, ref StartupInfo startup, out ProcessInfo process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(nint thread);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(nint process, uint code);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);

    private ProcessInfo info;
    private bool resumed;
    internal int Pid => info.Pid;
    internal SuspendedProcess(string image, string[] arguments, string directory)
    {
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Desktop = @"winsta0\default" };
        var command = new StringBuilder(Launcher.Arguments(new[] { image }.Concat(arguments)));
        // CREATE_SUSPENDED | CREATE_NEW_CONSOLE: never inherit a broker/worker console.
        if (!CreateProcessW(image, command, 0, 0, false, 0x14, 0, directory, ref startup, out info))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Create suspended child application failed.");
    }
    internal void VerifyAndResume(int session, bool elevated, string image)
    {
        using var process = System.Diagnostics.Process.GetProcessById(Pid);
        if (process.SessionId != session || Native.IsProcessElevated(Pid) != elevated ||
            !string.Equals(Native.ProcessImage(Pid), image, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Suspended application failed session, elevation or executable verification; it was not allowed to run.");
        if (ResumeThread(info.Thread) == uint.MaxValue)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Resuming verified child application failed.");
        resumed = true;
    }
    public void Dispose()
    {
        try
        {
            if (info.Process != 0 && !resumed)
            {
                if (!TerminateProcess(info.Process, 1))
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Cannot clean up suspended process {Pid}; it has not been resumed.");
                if (WaitForSingleObject(info.Process, 5000) != 0)
                    throw new InvalidOperationException($"Suspended process {Pid} termination was requested but not confirmed.");
            }
        }
        finally
        {
            if (info.Thread != 0) CloseHandle(info.Thread);
            if (info.Process != 0) CloseHandle(info.Process);
            info = default;
        }
    }
}

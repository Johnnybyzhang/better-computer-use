using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace BetterComputerUse;

internal static class Native
{
    [DllImport("wtsapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WTSIsChildSessionsEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);
    [DllImport("wtsapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WTSEnableChildSessions([MarshalAs(UnmanagedType.Bool)] bool enabled);
    [DllImport("wtsapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSGetChildSessionId(out uint session);
    [DllImport("wtsapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WTSLogoffSession(nint server, uint session, [MarshalAs(UnmanagedType.Bool)] bool wait);
    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformationW(nint server, int session, int infoClass, out nint buffer, out int bytes);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(nint buffer);

    internal static int SessionConnectionState(int session)
    {
        Check(WTSQuerySessionInformationW(0, session, 8, out var buffer, out var bytes));
        try { return bytes >= 4 ? Marshal.ReadInt32(buffer) : throw new InvalidDataException("Windows returned no session connection state."); }
        finally { WTSFreeMemory(buffer); }
    }
    internal static void VerifyChildSession(int session)
    {
        if (session <= 0 || session == CurrentSession || ChildSession() != session)
            throw new UnauthorizedAccessException("The desktop is not this parent session's current child.");
        string Query(int kind)
        {
            Check(WTSQuerySessionInformationW(0, session, kind, out var buffer, out _));
            try { return Marshal.PtrToStringUni(buffer) ?? ""; }
            finally { WTSFreeMemory(buffer); }
        }
        var user = Query(5); var domain = Query(7);
        var account = new NTAccount(domain, user);
        if (account.Translate(typeof(SecurityIdentifier)).Value != UserSid)
            throw new UnauthorizedAccessException("Child desktop belongs to a different user.");
    }

    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint session);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetNamedPipeServerSessionId(SafePipeHandle pipe, out uint session);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnableWindow(nint window, [MarshalAs(UnmanagedType.Bool)] bool enable);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowEnabled(nint window);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref uint size);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, out int value, int length, out int returned);

    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool FreeConsole();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] internal static extern int WindowStyle(nint window, int index);
    [DllImport("dwmapi.dll")] internal static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ReleaseCapture();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint pid);

    internal static int CurrentSession => Process.GetCurrentProcess().SessionId;
    internal static string UserSid { get { using var id = WindowsIdentity.GetCurrent(); return id.User!.Value; } }
    internal static bool Elevated { get { using var id = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); } }
    internal static void Check(bool ok) { if (!ok) throw new Win32Exception(Marshal.GetLastPInvokeError()); }
    internal static int? ChildSession()
    {
        if (WTSGetChildSessionId(out var id)) return id == uint.MaxValue ? null : checked((int)id);
        var error = Marshal.GetLastPInvokeError();
        if (error == 1168) return null;
        throw new Win32Exception(error, "Cannot query the current child session.");
    }

    internal static string ProcessImage(int pid)
    {
        using var handle = OpenProcess(0x1000, false, checked((uint)pid));
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        uint size = 32768;
        var path = new StringBuilder((int)size);
        Check(QueryFullProcessImageName(handle, 0, path, ref size));
        return path.ToString();
    }

    internal static DateTime ProcessStartUtc(int pid)
    {
        // Request only the query access that the elevated child explicitly grants
        // its unelevated same-user manager for this identity check.
        using var handle = OpenProcess(0x1000, false, checked((uint)pid));
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Cannot query process {pid} creation time.");
        Check(GetProcessTimes(handle, out var created, out _, out _, out _));
        return DateTime.FromFileTimeUtc(created);
    }

    internal static bool IsProcessElevated(int pid)
    {
        using var handle = OpenProcess(0x1000, false, checked((uint)pid));
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        Check(OpenProcessToken(handle, 8, out var token));
        using (token) { Check(GetTokenInformation(token, 20, out var elevation, sizeof(int), out _)); return elevation != 0; }
    }

    internal static Process VerifyClient(NamedPipeServerStream pipe, int session, DateTime earliestStart)
    {
        Check(GetNamedPipeClientSessionId(pipe.SafePipeHandle, out var actualSession));
        Check(GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid));
        if (actualSession != session) throw new UnauthorizedAccessException("Worker connected from a different Windows session.");
        string? sid = null;
        pipe.RunAsClient(() => sid = UserSid);
        if (sid != UserSid) throw new UnauthorizedAccessException("Worker user does not match the manager.");
        var process = Process.GetProcessById(checked((int)pid));
        try
        {
            if (process.SessionId != session || ProcessStartUtc(process.Id) < earliestStart ||
                !string.Equals(ProcessImage(process.Id), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Worker executable or process lifetime does not match.");
            return process;
        }
        catch { process.Dispose(); throw; }
    }

    internal static void VerifyServer(NamedPipeClientStream pipe, int parentPid, int parentSession, long parentStart)
    {
        Check(GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid));
        Check(GetNamedPipeServerSessionId(pipe.SafePipeHandle, out var session));
        using var parent = Process.GetProcessById(parentPid);
        if (pid != parentPid || session != parentSession || ProcessStartUtc(parentPid).Ticks != parentStart ||
            !string.Equals(ProcessImage(parentPid), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("IPC server is not the expected parent process.");
    }
}

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace BetterComputerUse;

internal static class ProcessQueryAccess
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Trustee { internal nint Multiple; internal int Operation, Form, Type; internal nint Sid; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Access { internal uint Rights; internal int Mode; internal uint Inheritance; internal Trustee Trustee; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityInfo(nint handle, int type, uint info,
        out nint owner, out nint group, out nint dacl, out nint sacl, out nint descriptor);
    [DllImport("advapi32.dll")] private static extern uint SetSecurityInfo(nint handle, int type, uint info,
        nint owner, nint group, nint dacl, nint sacl);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern uint SetEntriesInAclW(uint count, ref Access entry, nint oldAcl, out nint newAcl);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);

    // Task Scheduler's elevated child objects can omit the unelevated user's query
    // rights. Expose identity/liveness only, never memory, input, termination or token duplication.
    // Called only for our own worker and the helper it just created, after parent authentication.
    internal static void Grant(int pid)
    {
        if (!Native.Elevated) throw new UnauthorizedAccessException("Only an elevated worker configures its query access.");
        using var process = OpenProcess(0x60000 | 0x1000, false, pid);
        if (process.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        using var identity = WindowsIdentity.GetCurrent();
        var bytes = new byte[identity.User!.BinaryLength]; identity.User.GetBinaryForm(bytes, 0);
        var sid = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, sid, bytes.Length);
            GrantHandle(process.DangerousGetHandle(), sid, 0x1000 | 0x100000);
            Native.Check(OpenProcessToken(process, 0x60000 | 8, out var token));
            using (token) GrantHandle(token.DangerousGetHandle(), sid, 8);
        }
        finally { Marshal.FreeHGlobal(sid); }
    }
    private static void GrantHandle(nint handle, nint sid, uint rights)
    {
        var error = GetSecurityInfo(handle, 6, 4, out _, out _, out var oldAcl, out _, out var descriptor);
        if (error != 0) throw new Win32Exception((int)error);
        nint newAcl = 0;
        try
        {
            if (oldAcl == 0) throw new InvalidOperationException("Refusing to replace an absent object DACL.");
            var entry = new Access { Rights = rights, Mode = 1, Trustee = new Trustee { Form = 0, Type = 1, Sid = sid } };
            error = SetEntriesInAclW(1, ref entry, oldAcl, out newAcl);
            if (error != 0) throw new Win32Exception((int)error);
            error = SetSecurityInfo(handle, 6, 4, 0, 0, newAcl, 0);
            if (error != 0) throw new Win32Exception((int)error);
        }
        finally { if (newAcl != 0) LocalFree(newAcl); LocalFree(descriptor); }
    }
}

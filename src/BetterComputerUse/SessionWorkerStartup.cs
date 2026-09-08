using System.ComponentModel;

namespace BetterComputerUse;

internal static class SessionWorkerStartup
{
    internal static async Task<T> RequestUacAsync<T>(Func<Task<T>> request)
    {
        try { return await request(); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { throw new UacDeclinedException(); }
    }

    internal static async Task<(T Worker, string Mode, bool UacDeclined)> StartAsync<T>(string mode,
        Func<Task<T>> admin, Func<Task<T>> user)
    {
        if (mode == "user") return (await user(), "user", false);
        if (mode != "admin") throw new ArgumentException("Unknown worker mode.");
        try { return (await admin(), "admin", false); }
        catch (UacDeclinedException) { return (await user(), "user", true); }
    }
}

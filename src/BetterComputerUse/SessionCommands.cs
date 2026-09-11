using System.Text.Json.Nodes;
namespace BetterComputerUse;
internal static class SessionCommands
{
    internal static async Task<object> CallAsync(SessionManager manager, string name, JsonObject args) => name switch
    {
        "session_status" => await manager.StatusAsync(),
        "session_restart_worker" => await manager.RestartWorkerAsync(args),
        "session_start" => await manager.StartAsync(args["width"]?.GetValue<int>() ?? 1920, args["height"]?.GetValue<int>() ?? 1080,
            args["showViewer"]?.GetValue<bool>() ?? true, args["enableChildSessions"]?.GetValue<bool>() ?? false, args["mode"]?.GetValue<string>() ?? "admin"),
        "session_viewer" => await manager.ViewerAsync(args),
        "session_stop" => await manager.StopAsync(args),
        "session_logoff" => await manager.LogoffDisconnectedAsync(args),
        "computer_use" => await manager.ComputerAsync(args, false),
        "launch_process_as_admin" => await manager.LaunchProcessAsAdminAsync(args),
        "computer_use_elevated"  => await manager.ComputerAsync(args, true),
        _ when ComputerMethods.Allowed.Contains(name, StringComparer.Ordinal) => await manager.ComputerAsync(NativeToolCatalog.ForwardArguments(name, args), false),
        _ => throw new ArgumentException("Unknown tool.")
    };

}

using System.Text.Json;

namespace BetterComputerUse;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.FirstOrDefault() == "elevation-broker")
            {
                ElevationBroker.RunAsync(args).GetAwaiter().GetResult();
                return 0;
            }
            if (args.FirstOrDefault() is "worker" or "elevated-worker" or "session-worker" or "session-user-worker")
            {
                Worker.RunAsync(args, args[0] is "elevated-worker" or "session-worker").GetAwaiter().GetResult();
                return 0;
            }
            if (args.Length == 0 || args.Contains("--help"))
            {
                Console.WriteLine("BetterComputerUse mcp (--auto-helper | --helper <installed codex-computer-use.exe>) [--allow-elevation]\nBetterComputerUse doctor [--auto-helper | --helper <path>]");
                return 0;
            }
            HelperIdentity? helper = null;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--helper" && i + 1 < args.Length) helper = HelperIdentity.Load(args[++i]);
                else if (args[i] is not ("--allow-elevation" or "--auto-helper")) throw new ArgumentException($"Unknown option: {args[i]}");
            }
            if (args.Contains("--auto-helper")) helper ??= HelperIdentity.FindInstalled();
            if (args[0] == "doctor")
            {
                Native.Check(Native.WTSIsChildSessionsEnabled(out var enabled));
                Console.WriteLine(JsonSerializer.Serialize(new { parentSessionId = Native.CurrentSession, elevated = Native.Elevated,
                    childSessionsEnabled = enabled, existingChildSessionId = Native.ChildSession(), helper,
                    rdpActiveXInstalled = Type.GetTypeFromCLSID(new Guid("A0C63C30-F08D-4AB4-907C-34905D770C7D")) is not null }, Wire.Json));
                return 0;
            }
            if (args[0] is not ("mcp" or "desktop-host")) throw new ArgumentException("Use mcp or doctor (desktop-host is an internal role).");
            if (Native.Elevated) throw new InvalidOperationException("The MCP manager must run unelevated. Elevation is a separate worker role.");
            if (args[0] == "mcp")
            {
                using var client = new DesktopClient(args.Skip(1).ToArray());
                new McpServer(client, args.Contains("--allow-elevation")).RunAsync().GetAwaiter().GetResult();
                return 0;
            }
            helper ??= HelperIdentity.FindInstalled();
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            using var dispatcher = new Control();
            dispatcher.CreateControl();
            using var loop = new ApplicationContext();
            int exitCode = 0;
            _ = Task.Run(async () =>
            {
                try
                {
                    await using var manager = new SessionManager(dispatcher, helper, args.Contains("--allow-elevation"));
                    await DesktopHost.RunAsync(manager);
                }
                catch (Exception ex) { Console.Error.WriteLine(ex.GetBaseException().Message); exitCode = 1; }
                finally { await dispatcher.InvokeAsync(loop.ExitThread); }
            });
            Application.Run(loop);
            return exitCode;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.GetBaseException().Message); return 1; }
    }
}

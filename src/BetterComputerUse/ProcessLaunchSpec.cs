using System.Diagnostics;
using System.Text.Json.Nodes;

namespace BetterComputerUse;

internal sealed record ProcessLaunchSpec(HelperIdentity Image, string[] Arguments, string WorkingDirectory)
{
    internal static ProcessLaunchSpec FromArguments(JsonObject args)
    {
        var path = args.RequiredString("executablePath");
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("executablePath must be an absolute local .exe path.");
        var image = HelperIdentity.Load(path);
        var arguments = args["arguments"] is null ? [] :
            (args["arguments"] as JsonArray ?? throw new ArgumentException("arguments must be an array of strings."))
                .Select(value => value?.GetValue<string>() ?? throw new ArgumentException("Arguments cannot be null.")).ToArray();
        if (arguments.Length > 256 || arguments.Any(value => value.Contains('\0')) || Launcher.Arguments(arguments).Length + path.Length > 30000)
            throw new ArgumentException("Process arguments exceed the supported Windows command-line size or contain NUL.");
        var directory = args["workingDirectory"]?.GetValue<string>() ?? Path.GetDirectoryName(path)!;
        if (!Path.IsPathFullyQualified(directory) || directory.StartsWith(@"\\", StringComparison.Ordinal) || !Directory.Exists(directory))
            throw new ArgumentException("workingDirectory must be an existing absolute local directory.");
        return new(image, arguments, Path.GetFullPath(directory));
    }

    internal JsonObject LaunchAsAdmin(int expectedSession)
    {
        if (Native.CurrentSession != expectedSession || expectedSession == 0 || !Native.Elevated)
            throw new UnauthorizedAccessException("Admin process launch must originate inside the bound child session.");
        int? pid = null;
        try
        {
            using var imageLock = File.Open(Image.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Image.Verify();
            // The worker was elevated through UAC in the main session. Inherit its token here;
            // never request a UAC dialog from the child desktop.
            using var process = new SuspendedProcess(Image.Path, Arguments, WorkingDirectory);
            pid = process.Pid;
            process.VerifyAndResume(expectedSession, true, Image.Path);
            return new JsonObject { ["ok"] = true, ["result"] = new JsonObject { ["pid"] = pid, ["sessionId"] = expectedSession,
                ["elevated"] = true, ["executablePath"] = Image.Path, ["sha256"] = Image.Sha256 } };
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new JsonObject { ["ok"] = false, ["pid"] = pid,
                ["error"] = "Child application launch failed; do not automatically retry or launch on the main desktop: " + ex.Message };
        }
    }
}

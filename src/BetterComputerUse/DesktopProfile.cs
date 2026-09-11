using System.Text.Json;

namespace BetterComputerUse;

internal sealed record DesktopProfile(int SessionId, int Width, int Height)
{
    private static string FileName => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BetterComputerUse", $"desktop-{Native.CurrentSession}.json");
    internal static DesktopProfile? Load(int sessionId, string? path = null)
    {
        try
        {
            var profile = JsonSerializer.Deserialize<DesktopProfile>(File.ReadAllText(path ?? FileName));
            return profile is { Width: >= 640 and <= 4096, Height: >= 480 and <= 4096 } && profile.SessionId == sessionId ? profile : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    internal void Save(string? path = null)
    {
        path ??= FileName;
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(this));
            File.Move(temporary, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
    internal static void Clear()
    {
        try { File.Delete(FileName); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

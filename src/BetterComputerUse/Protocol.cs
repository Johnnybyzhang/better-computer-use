using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BetterComputerUse;

public static class Wire
{
    public const int MaxFrame = 64 * 1024 * 1024;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task WriteAsync(Stream stream, object value, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (payload.Length > MaxFrame) throw new InvalidDataException("IPC frame exceeds 64 MiB.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task<JsonObject> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct);
        var size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size is <= 0 or > MaxFrame) throw new InvalidDataException("Invalid IPC frame size.");
        var payload = new byte[size];
        await stream.ReadExactlyAsync(payload, ct);
        return JsonNode.Parse(payload) as JsonObject ?? throw new InvalidDataException("Expected JSON object.");
    }

    // ReadLineAsync alone allows an unbounded allocation before a size check.
    public static async Task<string?> ReadLineAsync(TextReader reader, int max, CancellationToken ct)
    {
        var result = new StringBuilder();
        var buffer = new char[1];
        while (await reader.ReadAsync(buffer.AsMemory(), ct) != 0)
        {
            if (buffer[0] == '\n') return result.ToString().TrimEnd('\r');
            if (result.Length >= max) throw new InvalidDataException("JSON line exceeds limit.");
            result.Append(buffer[0]);
        }
        return result.Length == 0 ? null : throw new EndOfStreamException("Truncated JSON line.");
    }

    public static string RequiredString(this JsonObject obj, string key) =>
        obj[key]?.GetValue<string>() is { Length: > 0 } s ? s : throw new ArgumentException($"{key} is required.");
}

public sealed record Binding(int SessionId, string Generation)
{
    public void Validate(JsonObject request)
    {
        if (request["sessionId"]?.GetValue<int>() != SessionId || request["generation"]?.GetValue<string>() != Generation)
            throw new InvalidOperationException("Stale or incorrect session binding; call session_status.");
    }
}

public static class ComputerMethods
{
    public static readonly string[] Allowed = ["list_apps", "list_windows", "get_window", "get_window_state",
        "activate_window", "launch_app", "click", "click_element", "scroll", "drag", "press_key", "type_text",
        "perform_secondary_action", "set_value", "start_audio_recording", "stop_audio_recording", "end_turn"];

    public static void Validate(string method, JsonObject args)
    {
        if (!Allowed.Contains(method, StringComparer.Ordinal)) throw new ArgumentException("Unsupported computer-use method.");
        if (method is "list_apps" or "list_windows" or "end_turn" or "start_audio_recording" or "stop_audio_recording") return;
        if (method == "launch_app") { _ = args.RequiredString("app"); return; }
        if (method == "get_window") { _ = args["id"]?.GetValue<long>() ?? throw new ArgumentException("id is required."); return; }
        var window = args["window"] as JsonObject ?? throw new ArgumentException("window is required.");
        _ = window.RequiredString("app");
        if (window["id"]?.GetValue<long>() is not >= 0) throw new ArgumentException("window.id must be nonnegative.");
    }
}

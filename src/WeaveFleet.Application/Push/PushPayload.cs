using System.Text.Json;
using System.Text.Json.Serialization;

namespace WeaveFleet.Application.Push;

/// <summary>
/// What a Fleet push carries, version 1. Small (well under 1 KB) and generic: which machine and session, what kind
/// of notification, a title and a line, and where tapping goes. Never a token or a file's contents. The service
/// worker reads it in client/src/lib/push/payload.ts.
/// </summary>
/// <param name="Url">A path on the phone's home machine: <c>/phone/s/&lt;machineId&gt;/&lt;sessionId&gt;?ask=&lt;requestId&gt;</c>.</param>
/// <param name="Tag">Repeats with the same tag replace each other: <c>&lt;machineId&gt;:&lt;sessionId&gt;</c>.</param>
public sealed record PushPayload(
    int V,
    string MachineId,
    string MachineName,
    string SessionId,
    string Kind,
    string Reason,
    string Title,
    string Body,
    string Url,
    string Tag,
    string? RequestId = null)
{
    public const int MaxTitleLength = 80;
    public const int MaxBodyLength = 140;

    public string ToJson() => JsonSerializer.Serialize(this, PushPayloadJsonContext.Default.PushPayload);

    /// <summary>Where tapping a notification about this session goes on the phone.</summary>
    public static string UrlFor(string machineId, string sessionId, string? requestId) =>
        $"/phone/s/{Uri.EscapeDataString(machineId)}/{Uri.EscapeDataString(sessionId)}"
        + (string.IsNullOrEmpty(requestId) ? string.Empty : $"?ask={Uri.EscapeDataString(requestId)}");

    /// <summary>Shortens <paramref name="text"/> to <paramref name="max"/> characters, ending in an ellipsis when cut.</summary>
    public static string Clip(string text, int max)
    {
        var trimmed = text.ReplaceLineEndings(" ").Trim();
        return trimmed.Length <= max ? trimmed : string.Concat(trimmed.AsSpan(0, max - 1).TrimEnd(), "…");
    }
}

[JsonSerializable(typeof(PushPayload))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class PushPayloadJsonContext : JsonSerializerContext
{
}

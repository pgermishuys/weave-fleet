using System.Text.Json;
using System.Text.Json.Serialization;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode2;

// ── V2 API ──────────────────────────────────────────────────────────────────
// Only the fields Fleet reads. V2's API calls itself experimental, so everything is optional and unknown
// fields are ignored.

/// <summary>V2 wraps every JSON response in <c>{ "data": … }</c>.</summary>
internal sealed record OpenCode2Envelope<T>
{
    public T? Data { get; init; }
}

/// <summary><c>GET /api/info</c>.</summary>
internal sealed record OpenCode2ServerInfo
{
    public string? Version { get; init; }
    public int? Pid { get; init; }
}

/// <summary>A V2 session (<c>Session.Info</c>).</summary>
internal sealed record OpenCode2SessionInfo
{
    public string? Id { get; init; }
    public string? ParentID { get; init; }
    public string? Title { get; init; }
    public OpenCode2Location? Location { get; init; }
}

/// <summary>Where a V2 session runs: V2 serves every directory from one server.</summary>
internal sealed record OpenCode2Location
{
    public required string Directory { get; init; }
}

/// <summary><c>POST /api/session</c>.</summary>
internal sealed record OpenCode2CreateSessionRequest
{
    public required OpenCode2Location Location { get; init; }
}

/// <summary><c>POST /api/session/{id}/prompt</c>. <see cref="Id"/> names the user message, so it matches Fleet's own.</summary>
internal sealed record OpenCode2PromptRequest
{
    public string? Id { get; init; }
    public required string Text { get; init; }
}

/// <summary><c>POST /api/session/{id}/interrupt</c>.</summary>
internal sealed record OpenCode2InterruptResponse
{
    public bool Interrupted { get; init; }
}

/// <summary>One event from <c>GET /api/event</c>: <c>{ id, created, type, location?, data, durable? }</c>.</summary>
internal sealed record OpenCode2Event
{
    public string? Id { get; init; }
    public long? Created { get; init; }
    public required string Type { get; init; }
    public JsonElement Data { get; init; }

    /// <summary>The V2 session the event is about, when it's about one.</summary>
    [JsonIgnore]
    public string? SessionId =>
        Data.ValueKind == JsonValueKind.Object
        && Data.TryGetProperty("sessionID", out var id)
        && id.ValueKind == JsonValueKind.String
            ? id.GetString()
            : null;
}

// ── Fleet event payloads ────────────────────────────────────────────────────
// The shapes Fleet's translator and client read (message.updated, message.part.updated, message.part.delta,
// session.status, session.idle, session.error).

internal sealed record OpenCode2StatusPayload
{
    [JsonPropertyName("sessionID")] public required string SessionId { get; init; }
    public required OpenCode2Status Status { get; init; }
}

/// <summary>
/// <c>busy</c>, <c>idle</c> or <c>retry</c>. A retry carries <see cref="Count"/>, <see cref="Reason"/> and
/// <see cref="Delay"/> (milliseconds), which the relay reads for the retry banner.
/// </summary>
internal sealed record OpenCode2Status
{
    public required string Type { get; init; }
    public int? Count { get; init; }
    public string? Reason { get; init; }
    public long? Delay { get; init; }
}

internal sealed record OpenCode2SessionPayload
{
    [JsonPropertyName("sessionID")] public required string SessionId { get; init; }
}

internal sealed record OpenCode2ErrorPayload
{
    [JsonPropertyName("sessionID")] public required string SessionId { get; init; }
    public required OpenCode2ErrorInfo Error { get; init; }
}

/// <summary>A failure as Fleet's error reader takes it: a name and a message.</summary>
internal sealed record OpenCode2ErrorInfo
{
    public required string Name { get; init; }
    public required string Message { get; init; }
}

internal sealed record OpenCode2MessageUpdatedPayload
{
    public required OpenCode2MessageInfo Info { get; init; }
}

internal sealed record OpenCode2MessageInfo
{
    public required string Id { get; init; }
    public required string Role { get; init; }
    [JsonPropertyName("sessionID")] public required string SessionId { get; init; }
    public string? Agent { get; init; }
    [JsonPropertyName("modelID")] public string? ModelId { get; init; }
    [JsonPropertyName("providerID")] public string? ProviderId { get; init; }
    public required OpenCode2MessageTime Time { get; init; }
    public double? Cost { get; init; }
    public OpenCode2Tokens? Tokens { get; init; }
    public string? Finish { get; init; }
}

internal sealed record OpenCode2MessageTime
{
    public required long Created { get; init; }
    public long? Completed { get; init; }
}

internal sealed record OpenCode2Tokens
{
    public double Input { get; init; }
    public double Output { get; init; }
    public double Reasoning { get; init; }
}

internal sealed record OpenCode2PartUpdatedPayload
{
    [JsonPropertyName("sessionID")] public required string SessionId { get; init; }
    public required OpenCode2Part Part { get; init; }
}

/// <summary>A message part: <c>text</c> and <c>reasoning</c> carry <see cref="Text"/>; <c>step-start</c> and <c>step-finish</c> the rest.</summary>
/// <remarks><see cref="Type"/> comes first: Fleet reads parts polymorphically, and the discriminator must lead.</remarks>
internal sealed record OpenCode2Part
{
    public required string Type { get; init; }
    public required string Id { get; init; }
    [JsonPropertyName("sessionID")] public required string SessionId { get; init; }
    [JsonPropertyName("messageID")] public required string MessageId { get; init; }
    public string? Text { get; init; }
    public int? Index { get; init; }
    public string? Reason { get; init; }
    public double? Cost { get; init; }
    public OpenCode2Tokens? Tokens { get; init; }
    public long? CompletedAt { get; init; }
}

internal sealed record OpenCode2PartDeltaPayload
{
    [JsonPropertyName("sessionID")] public required string SessionId { get; init; }
    [JsonPropertyName("messageID")] public required string MessageId { get; init; }
    [JsonPropertyName("partID")] public required string PartId { get; init; }
    public required string Field { get; init; }
    public required string Delta { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(OpenCode2Envelope<OpenCode2ServerInfo>))]
[JsonSerializable(typeof(OpenCode2Envelope<OpenCode2SessionInfo>))]
[JsonSerializable(typeof(OpenCode2Envelope<Dictionary<string, JsonElement>>))]
[JsonSerializable(typeof(OpenCode2ServerInfo))]
[JsonSerializable(typeof(OpenCode2CreateSessionRequest))]
[JsonSerializable(typeof(OpenCode2PromptRequest))]
[JsonSerializable(typeof(OpenCode2InterruptResponse))]
[JsonSerializable(typeof(OpenCode2Event))]
[JsonSerializable(typeof(OpenCode2StatusPayload))]
[JsonSerializable(typeof(OpenCode2SessionPayload))]
[JsonSerializable(typeof(OpenCode2ErrorPayload))]
[JsonSerializable(typeof(OpenCode2MessageUpdatedPayload))]
[JsonSerializable(typeof(OpenCode2PartUpdatedPayload))]
[JsonSerializable(typeof(OpenCode2PartDeltaPayload))]
internal sealed partial class OpenCode2JsonContext : JsonSerializerContext
{
}

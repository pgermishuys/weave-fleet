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

    /// <summary>The session's own permission rules, which win over the user's config.</summary>
    public IReadOnlyList<OpenCode2PermissionRule>? Permissions { get; init; }
}

/// <summary>One rule of a V2 <c>Permission.Ruleset</c>: <c>effect</c> is <c>allow</c>, <c>deny</c> or <c>ask</c>.</summary>
internal sealed record OpenCode2PermissionRule
{
    public required string Action { get; init; }
    public required string Resource { get; init; }
    public required string Effect { get; init; }
}

/// <summary><c>POST /api/session/{id}/permission/{requestID}/reply</c>: <c>once</c>, <c>always</c> or <c>reject</c>.</summary>
internal sealed record OpenCode2PermissionReply
{
    public required string Decision { get; init; }
}

/// <summary>
/// A V2 form (<c>Form.Info</c>), which is how the question tool asks: <c>metadata.kind</c> is <c>question</c> and
/// <c>metadata.tool</c> names the tool call. Each question is a field (<c>q0</c>, <c>q1</c>, …).
/// </summary>
internal sealed record OpenCode2Form
{
    public string? Id { get; init; }
    [JsonPropertyName("sessionID")] public string? SessionId { get; init; }
    public string? Title { get; init; }
    public JsonElement Metadata { get; init; }
    public IReadOnlyList<OpenCode2FormField>? Fields { get; init; }

    /// <summary>The tool call that asked, when the form is a question.</summary>
    [JsonIgnore]
    public string? ToolCallId =>
        Metadata.ValueKind == JsonValueKind.Object
        && Metadata.TryGetProperty("tool", out var tool)
        && tool.ValueKind == JsonValueKind.Object
        && tool.TryGetProperty("id", out var id)
        && id.ValueKind == JsonValueKind.String
            ? id.GetString()
            : null;

    [JsonIgnore]
    public bool IsQuestion =>
        Metadata.ValueKind == JsonValueKind.Object
        && Metadata.TryGetProperty("kind", out var kind)
        && kind.ValueKind == JsonValueKind.String
        && kind.GetString() == "question";
}

/// <summary>One form field: <c>string</c> (one answer, <c>custom</c> allows free text) or <c>multiselect</c> (several).</summary>
internal sealed record OpenCode2FormField
{
    public required string Key { get; init; }
    public string? Type { get; init; }
    public IReadOnlyList<OpenCode2FormOption>? Options { get; init; }
}

/// <summary>A choice in a form field: the question tool's options, with <see cref="Value"/> what the reply sends.</summary>
internal sealed record OpenCode2FormOption
{
    public string? Value { get; init; }
    public string? Label { get; init; }
}

/// <summary><c>POST /api/session/{id}/form/{formID}/reply</c>: one value per field key.</summary>
internal sealed record OpenCode2FormReply
{
    public required Dictionary<string, JsonElement> Answer { get; init; }
}

/// <summary><c>GET /api/session/{id}/message</c>: a page of messages, newest first, with cursors for the next page.</summary>
internal sealed record OpenCode2MessagePage
{
    public IReadOnlyList<OpenCode2Message>? Data { get; init; }
    public OpenCode2Cursor? Cursor { get; init; }
}

internal sealed record OpenCode2Cursor
{
    public string? Previous { get; init; }
    public string? Next { get; init; }
}

/// <summary>
/// A V2 message (<c>Session.Message.Info</c>). Fleet reads <c>user</c> (<see cref="Text"/>) and <c>assistant</c>
/// (one step: <see cref="Content"/> is its text, reasoning and tool calls in order); the other types (<c>idle</c>,
/// <c>shell</c>, <c>compaction</c>, <c>synthetic</c>, …) aren't shown.
/// </summary>
internal sealed record OpenCode2Message
{
    public string? Id { get; init; }
    public string? Type { get; init; }
    public OpenCode2MessageTimes? Time { get; init; }
    public string? Text { get; init; }
    public string? Agent { get; init; }
    public OpenCode2ModelRef? Model { get; init; }
    public IReadOnlyList<OpenCode2Content>? Content { get; init; }
    public string? Finish { get; init; }
    public double? Cost { get; init; }
    public OpenCode2TokenUsage? Tokens { get; init; }
    public OpenCode2StructuredError? Error { get; init; }
}

internal sealed record OpenCode2MessageTimes
{
    public long? Created { get; init; }
    public long? Completed { get; init; }
}

internal sealed record OpenCode2ModelRef
{
    public string? Id { get; init; }
    [JsonPropertyName("providerID")] public string? ProviderId { get; init; }
}

internal sealed record OpenCode2TokenUsage
{
    public double? Input { get; init; }
    public double? Output { get; init; }
    public double? Reasoning { get; init; }
}

/// <summary>V2's <c>Session.StructuredError</c>.</summary>
internal sealed record OpenCode2StructuredError
{
    public string? Type { get; init; }
    public string? Message { get; init; }
}

/// <summary>One item of an assistant message: <c>text</c>, <c>reasoning</c> or <c>tool</c>.</summary>
internal sealed record OpenCode2Content
{
    public string? Type { get; init; }
    public string? Text { get; init; }
    public string? Id { get; init; }
    public string? Name { get; init; }
    public OpenCode2ToolState? State { get; init; }
}

/// <summary>
/// A tool call's state in history: <c>streaming</c> (input still arriving, as text), <c>running</c>,
/// <c>completed</c> or <c>error</c>.
/// </summary>
internal sealed record OpenCode2ToolState
{
    public string? Status { get; init; }
    public JsonElement Input { get; init; }
    public IReadOnlyList<OpenCode2ToolContent>? Content { get; init; }
    public JsonElement Metadata { get; init; }
    public OpenCode2StructuredError? Error { get; init; }
}

/// <summary>What a tool returned: <c>text</c>, or a <c>file</c> (<see cref="Uri"/>, <see cref="Mime"/>).</summary>
internal sealed record OpenCode2ToolContent
{
    public string? Type { get; init; }
    public string? Text { get; init; }
    public string? Uri { get; init; }
    public string? Mime { get; init; }
    public string? Name { get; init; }
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

    /// <summary>
    /// The V2 session the event is about, when it's about one. Most events say so in <c>data.sessionID</c>;
    /// <c>form.created</c> says it in <c>data.form.sessionID</c>.
    /// </summary>
    [JsonIgnore]
    public string? SessionId
    {
        get
        {
            if (Data.ValueKind != JsonValueKind.Object)
                return null;
            var owner = Data.TryGetProperty("form", out var form) && form.ValueKind == JsonValueKind.Object ? form : Data;
            return owner.TryGetProperty("sessionID", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;
        }
    }
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

/// <summary>
/// A message part: <c>text</c> and <c>reasoning</c> carry <see cref="Text"/>; <c>tool</c> carries
/// <see cref="Tool"/>, <see cref="CallId"/> and <see cref="State"/>; <c>file</c> a <see cref="Mime"/> and
/// <see cref="Url"/>; <c>step-start</c> and <c>step-finish</c> the rest.
/// </summary>
/// <remarks><see cref="Type"/> comes first: Fleet reads parts polymorphically, and the discriminator must lead.</remarks>
internal sealed record OpenCode2Part
{
    public required string Type { get; init; }
    public required string Id { get; init; }
    [JsonPropertyName("sessionID")] public required string SessionId { get; init; }
    [JsonPropertyName("messageID")] public required string MessageId { get; init; }
    public string? Text { get; init; }
    public string? Tool { get; init; }
    [JsonPropertyName("callID")] public string? CallId { get; init; }
    public OpenCode2ToolPartState? State { get; init; }
    public string? Mime { get; init; }
    public string? Url { get; init; }
    public string? Filename { get; init; }
    public int? Index { get; init; }
    public string? Reason { get; init; }
    public double? Cost { get; init; }
    public OpenCode2Tokens? Tokens { get; init; }
    public long? CompletedAt { get; init; }
}

/// <summary>
/// A tool part's state as Fleet reads it: <c>pending</c>, <c>running</c>, <c>completed</c> (with
/// <see cref="Output"/>) or <c>error</c> (with <see cref="Error"/>).
/// </summary>
/// <remarks><see cref="Status"/> comes first, for the same reason as a part's type.</remarks>
internal sealed record OpenCode2ToolPartState
{
    public required string Status { get; init; }
    public JsonElement? Input { get; init; }
    public JsonElement? Output { get; init; }
    public string? Error { get; init; }
    public JsonElement? Metadata { get; init; }
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
[JsonSerializable(typeof(OpenCode2Envelope<List<OpenCode2Form>>))]
[JsonSerializable(typeof(OpenCode2MessagePage))]
[JsonSerializable(typeof(OpenCode2Form))]
[JsonSerializable(typeof(List<OpenCode2ToolContent>))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(OpenCode2FormReply))]
[JsonSerializable(typeof(OpenCode2PermissionReply))]
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

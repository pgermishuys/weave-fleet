using System.Text.Json;
using System.Text.Json.Serialization;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode;

/// <summary>Shared <see cref="JsonSerializerOptions"/> for all OpenCode API serialization.</summary>
internal static class OpenCodeJsonOptions
{
    internal static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

// ---------------------------------------------------------------------------
// Health
// ---------------------------------------------------------------------------

/// <summary>Response from GET /global/health.</summary>
internal sealed record OpenCodeHealthResponse
{
    [JsonPropertyName("healthy")] public bool Healthy { get; init; }
    [JsonPropertyName("version")] public string? Version { get; init; }
}

// ---------------------------------------------------------------------------
// Sessions
// ---------------------------------------------------------------------------

/// <summary>Full session object returned by the OpenCode API.</summary>
internal sealed record OpenCodeSessionInfo
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("slug")] public string? Slug { get; init; }
    [JsonPropertyName("projectId")] public string? ProjectId { get; init; }
    [JsonPropertyName("directory")] public string? Directory { get; init; }
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("version")] public string? Version { get; init; }
    [JsonPropertyName("time")] public OpenCodeSessionTime? Time { get; init; }
    [JsonPropertyName("parentId")] public string? ParentId { get; init; }
    [JsonPropertyName("workspaceId")] public string? WorkspaceId { get; init; }
    [JsonPropertyName("summary")] public string? Summary { get; init; }
    [JsonPropertyName("share")] public JsonElement? Share { get; init; }
    [JsonPropertyName("permission")] public JsonElement? Permission { get; init; }
    [JsonPropertyName("revert")] public JsonElement? Revert { get; init; }
}

/// <summary>Timestamps on a session.</summary>
internal sealed record OpenCodeSessionTime
{
    [JsonPropertyName("created")] public long Created { get; init; }
    [JsonPropertyName("updated")] public long Updated { get; init; }
    [JsonPropertyName("compacting")] public long? Compacting { get; init; }
    [JsonPropertyName("archived")] public long? Archived { get; init; }
}

/// <summary>Request body for POST /session.</summary>
internal sealed record OpenCodeCreateSessionRequest
{
    [JsonPropertyName("parentId")] public string? ParentId { get; init; }
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("permission")] public JsonElement? Permission { get; init; }
    [JsonPropertyName("workspaceId")] public string? WorkspaceId { get; init; }
}

// ---------------------------------------------------------------------------
// Messages
// ---------------------------------------------------------------------------

/// <summary>Wrapper returned by message endpoints — info + parts list.</summary>
internal sealed record OpenCodeMessageWithParts
{
    [JsonPropertyName("info")] public required OpenCodeMessageInfo Info { get; init; }
    [JsonPropertyName("parts")] public IReadOnlyList<OpenCodeMessagePart> Parts { get; init; } = [];
}

/// <summary>
/// Polymorphic base for user and assistant messages (discriminated by "role").
/// Non-abstract so that messages with an unknown or missing role discriminator
/// fall back to this base type instead of throwing <see cref="System.NotSupportedException"/>.
/// </summary>
[JsonPolymorphic(
    TypeDiscriminatorPropertyName = "role",
    IgnoreUnrecognizedTypeDiscriminators = true,
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FallBackToBaseType)]
[JsonDerivedType(typeof(OpenCodeUserMessage), "user")]
[JsonDerivedType(typeof(OpenCodeAssistantMessage), "assistant")]
internal record OpenCodeMessageInfo
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
    [JsonPropertyName("sessionId")] public string SessionId { get; init; } = string.Empty;
    /// <summary>
    /// Derived from the polymorphic type discriminator; not stored as a property.
    /// Defaults to <c>"unknown"</c> for messages with an unrecognised or missing role.
    /// </summary>
    [JsonIgnore]
    public virtual string Role => "unknown";
    [JsonPropertyName("time")] public OpenCodeMessageTime Time { get; init; } = new();
}

/// <summary>A user-submitted message.</summary>
internal sealed record OpenCodeUserMessage : OpenCodeMessageInfo
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Role => "user";
    [JsonPropertyName("agent")] public string? Agent { get; init; }
    [JsonPropertyName("model")] public OpenCodeModelRef? Model { get; init; }
    [JsonPropertyName("system")] public string? System { get; init; }
    [JsonPropertyName("format")] public string? Format { get; init; }
    [JsonPropertyName("tools")] public JsonElement? Tools { get; init; }
    [JsonPropertyName("variant")] public string? Variant { get; init; }
}

/// <summary>An assistant-generated message.</summary>
internal sealed record OpenCodeAssistantMessage : OpenCodeMessageInfo
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Role => "assistant";
    [JsonPropertyName("parentId")] public string? ParentId { get; init; }
    [JsonPropertyName("modelId")] public string? ModelId { get; init; }
    [JsonPropertyName("providerId")] public string? ProviderId { get; init; }
    [JsonPropertyName("agent")] public string? Agent { get; init; }
    [JsonPropertyName("path")] public OpenCodeMessagePath? Path { get; init; }
    [JsonPropertyName("cost")] public double? Cost { get; init; }
    [JsonPropertyName("tokens")] public OpenCodeTokenUsage? Tokens { get; init; }
    [JsonPropertyName("mode")] public string? Mode { get; init; }
    [JsonPropertyName("error")] public JsonElement? Error { get; init; }
    [JsonPropertyName("summary")] public string? Summary { get; init; }
    [JsonPropertyName("finish")] public string? Finish { get; init; }
    [JsonPropertyName("variant")] public string? Variant { get; init; }
}

/// <summary>Timestamps on a message.</summary>
internal sealed record OpenCodeMessageTime
{
    [JsonPropertyName("created")] public long Created { get; init; }
    [JsonPropertyName("completed")] public long? Completed { get; init; }
}

/// <summary>Model reference (provider + model IDs) — <b>read/response</b> path.</summary>
/// <remarks>
/// Fields are nullable and non-required because OpenCode may return messages where
/// the <c>model</c> object has missing or null fields (e.g., <c>"model": {}</c>).
/// OpenCode responses use camelCase (<c>providerId</c> / <c>modelId</c>).
/// For the write/request path see <see cref="OpenCodeModelRefRequest"/>.
/// </remarks>
internal sealed record OpenCodeModelRef
{
    [JsonPropertyName("providerId")] public string? ProviderId { get; init; }
    [JsonPropertyName("modelId")] public string? ModelId { get; init; }
}

/// <summary>Model reference (provider + model IDs) — <b>write/request</b> path.</summary>
/// <remarks>
/// OpenCode request validation expects <c>providerID</c> / <c>modelID</c> (uppercase D),
/// which differs from the response path casing. Used in <see cref="OpenCodePromptRequest"/>
/// and <see cref="OpenCodePromptSubtaskPart"/>.
/// </remarks>
internal sealed record OpenCodeModelRefRequest
{
    [JsonPropertyName("providerID")] public required string ProviderId { get; init; }
    [JsonPropertyName("modelID")] public required string ModelId { get; init; }
}

/// <summary>Working-directory paths associated with a message.</summary>
internal sealed record OpenCodeMessagePath
{
    [JsonPropertyName("cwd")] public string? Cwd { get; init; }
    [JsonPropertyName("root")] public string? Root { get; init; }
}

/// <summary>Token usage breakdown for an assistant message.</summary>
internal sealed record OpenCodeTokenUsage
{
    [JsonPropertyName("input")] public double Input { get; init; }
    [JsonPropertyName("output")] public double Output { get; init; }
    [JsonPropertyName("reasoning")] public double Reasoning { get; init; }
    [JsonPropertyName("cache")] public OpenCodeCacheTokens? Cache { get; init; }
    [JsonPropertyName("total")] public double? Total { get; init; }
}

/// <summary>Cache read/write token counts.</summary>
internal sealed record OpenCodeCacheTokens
{
    [JsonPropertyName("read")] public double Read { get; init; }
    [JsonPropertyName("write")] public double Write { get; init; }
}

// ---------------------------------------------------------------------------
// Message Parts
// ---------------------------------------------------------------------------

/// <summary>
/// Polymorphic base for all message content parts (discriminated by "type").
/// Non-abstract so that parts with an unknown or missing type discriminator
/// fall back to this base type instead of throwing <see cref="System.NotSupportedException"/>.
/// </summary>
[JsonPolymorphic(
    TypeDiscriminatorPropertyName = "type",
    IgnoreUnrecognizedTypeDiscriminators = true,
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FallBackToBaseType)]
[JsonDerivedType(typeof(OpenCodeTextPart), "text")]
[JsonDerivedType(typeof(OpenCodeToolPart), "tool")]
[JsonDerivedType(typeof(OpenCodeReasoningPart), "reasoning")]
[JsonDerivedType(typeof(OpenCodeStepStartPart), "step-start")]
[JsonDerivedType(typeof(OpenCodeStepFinishPart), "step-finish")]
[JsonDerivedType(typeof(OpenCodeFilePart), "file")]
[JsonDerivedType(typeof(OpenCodeAgentPart), "agent")]
[JsonDerivedType(typeof(OpenCodeSubtaskPart), "subtask")]
[JsonDerivedType(typeof(OpenCodeSnapshotPart), "snapshot")]
[JsonDerivedType(typeof(OpenCodePatchPart), "patch")]
[JsonDerivedType(typeof(OpenCodeRetryPart), "retry")]
[JsonDerivedType(typeof(OpenCodeCompactionPart), "compaction")]
internal record OpenCodeMessagePart
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
    [JsonPropertyName("sessionId")] public string SessionId { get; init; } = string.Empty;
    [JsonPropertyName("messageId")] public string MessageId { get; init; } = string.Empty;
}

/// <summary>Plain text content part.</summary>
internal sealed record OpenCodeTextPart : OpenCodeMessagePart
{
    [JsonPropertyName("text")] public string? Text { get; init; }
    [JsonPropertyName("synthetic")] public bool? Synthetic { get; init; }
    [JsonPropertyName("ignored")] public bool? Ignored { get; init; }
    [JsonPropertyName("time")] public OpenCodeMessageTime? Time { get; init; }
    [JsonPropertyName("metadata")] public JsonElement? Metadata { get; init; }
}

/// <summary>Tool invocation part.</summary>
internal sealed record OpenCodeToolPart : OpenCodeMessagePart
{
    [JsonPropertyName("callId")] public string? CallId { get; init; }
    [JsonPropertyName("tool")] public string? Tool { get; init; }
    [JsonPropertyName("state")] public OpenCodeToolState? State { get; init; }
    [JsonPropertyName("metadata")] public JsonElement? Metadata { get; init; }
}

/// <summary>Reasoning/thinking content part.</summary>
internal sealed record OpenCodeReasoningPart : OpenCodeMessagePart
{
    [JsonPropertyName("text")] public string? Text { get; init; }
    [JsonPropertyName("summary")] public string? Summary { get; init; }
}

/// <summary>Marks the start of an agent step.</summary>
internal sealed record OpenCodeStepStartPart : OpenCodeMessagePart
{
    [JsonPropertyName("index")] public int Index { get; init; }
}

/// <summary>Marks the finish of an agent step.</summary>
internal sealed record OpenCodeStepFinishPart : OpenCodeMessagePart
{
    [JsonPropertyName("index")] public int Index { get; init; }
    [JsonPropertyName("reason")] public string? Reason { get; init; }
}

/// <summary>A file/image attachment part.</summary>
internal sealed record OpenCodeFilePart : OpenCodeMessagePart
{
    [JsonPropertyName("mime")] public string? Mime { get; init; }
    [JsonPropertyName("url")] public string? Url { get; init; }
    [JsonPropertyName("filename")] public string? Filename { get; init; }
}

/// <summary>Sub-agent delegation part.</summary>
internal sealed record OpenCodeAgentPart : OpenCodeMessagePart
{
    [JsonPropertyName("agent")] public string? Agent { get; init; }
    [JsonPropertyName("input")] public string? Input { get; init; }
    [JsonPropertyName("output")] public string? Output { get; init; }
}

/// <summary>Subtask part.</summary>
internal sealed record OpenCodeSubtaskPart : OpenCodeMessagePart
{
    [JsonPropertyName("prompt")] public string? Prompt { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("agent")] public string? Agent { get; init; }
    [JsonPropertyName("metadata")] public JsonElement? Metadata { get; init; }
}

/// <summary>Workspace snapshot part.</summary>
internal sealed record OpenCodeSnapshotPart : OpenCodeMessagePart
{
    [JsonPropertyName("snapshot")] public string? Snapshot { get; init; }
}

/// <summary>Git patch part.</summary>
internal sealed record OpenCodePatchPart : OpenCodeMessagePart
{
    [JsonPropertyName("patch")] public string? Patch { get; init; }
}

/// <summary>Retry event part.</summary>
internal sealed record OpenCodeRetryPart : OpenCodeMessagePart
{
    [JsonPropertyName("reason")] public string? Reason { get; init; }
    [JsonPropertyName("delay")] public int? Delay { get; init; }
}

/// <summary>Context-compaction notification part.</summary>
internal sealed record OpenCodeCompactionPart : OpenCodeMessagePart
{
    [JsonPropertyName("summary")] public string? Summary { get; init; }
}

// ---------------------------------------------------------------------------
// Tool State
// ---------------------------------------------------------------------------

/// <summary>
/// Polymorphic tool invocation state (discriminated by "status").
/// Non-abstract so that states with an unknown or missing status discriminator
/// fall back to this base type instead of throwing <see cref="System.NotSupportedException"/>.
/// </summary>
[JsonPolymorphic(
    TypeDiscriminatorPropertyName = "status",
    IgnoreUnrecognizedTypeDiscriminators = true,
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FallBackToBaseType)]
[JsonDerivedType(typeof(OpenCodeToolPending), "pending")]
[JsonDerivedType(typeof(OpenCodeToolRunning), "running")]
[JsonDerivedType(typeof(OpenCodeToolCompleted), "completed")]
[JsonDerivedType(typeof(OpenCodeToolError), "error")]
internal record OpenCodeToolState;

/// <summary>Tool call is queued but not started.</summary>
internal sealed record OpenCodeToolPending : OpenCodeToolState
{
    [JsonPropertyName("input")] public JsonElement? Input { get; init; }
}

/// <summary>Tool call is currently executing.</summary>
internal sealed record OpenCodeToolRunning : OpenCodeToolState
{
    [JsonPropertyName("input")] public JsonElement? Input { get; init; }
}

/// <summary>Tool call finished successfully.</summary>
internal sealed record OpenCodeToolCompleted : OpenCodeToolState
{
    [JsonPropertyName("input")] public JsonElement? Input { get; init; }
    [JsonPropertyName("output")] public JsonElement? Output { get; init; }
    [JsonPropertyName("metadata")] public JsonElement? Metadata { get; init; }
}

/// <summary>Tool call failed.</summary>
internal sealed record OpenCodeToolError : OpenCodeToolState
{
    [JsonPropertyName("input")] public JsonElement? Input { get; init; }
    [JsonPropertyName("output")] public string? Output { get; init; }
}

// ---------------------------------------------------------------------------
// Prompt Request
// ---------------------------------------------------------------------------

/// <summary>Request body for POST /session/:id/message or prompt_async.</summary>
internal sealed record OpenCodePromptRequest
{
    [JsonPropertyName("parts")] public required IReadOnlyList<OpenCodePromptPart> Parts { get; init; }
    [JsonPropertyName("agent")] public string? Agent { get; init; }
    [JsonPropertyName("model")] public OpenCodeModelRefRequest? Model { get; init; }
    [JsonPropertyName("noReply")] public bool? NoReply { get; init; }
    [JsonPropertyName("format")] public string? Format { get; init; }
    [JsonPropertyName("system")] public string? System { get; init; }
    [JsonPropertyName("variant")] public string? Variant { get; init; }
}

/// <summary>Polymorphic prompt part (discriminated by "type").</summary>
[JsonPolymorphic(
    TypeDiscriminatorPropertyName = "type",
    IgnoreUnrecognizedTypeDiscriminators = true,
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FallBackToBaseType)]
[JsonDerivedType(typeof(OpenCodePromptTextPart), "text")]
[JsonDerivedType(typeof(OpenCodePromptFilePart), "file")]
[JsonDerivedType(typeof(OpenCodePromptAgentPart), "agent")]
[JsonDerivedType(typeof(OpenCodePromptSubtaskPart), "subtask")]
internal record OpenCodePromptPart
{
    [JsonPropertyName("id")] public string? Id { get; init; }
}

/// <summary>Text prompt part.</summary>
internal sealed record OpenCodePromptTextPart : OpenCodePromptPart
{
    [JsonPropertyName("text")] public required string Text { get; init; }
    [JsonPropertyName("synthetic")] public bool? Synthetic { get; init; }
    [JsonPropertyName("ignored")] public bool? Ignored { get; init; }
}

/// <summary>File/image prompt part.</summary>
internal sealed record OpenCodePromptFilePart : OpenCodePromptPart
{
    [JsonPropertyName("mime")] public required string Mime { get; init; }
    [JsonPropertyName("url")] public required string Url { get; init; }
    [JsonPropertyName("filename")] public string? Filename { get; init; }
}

/// <summary>Agent delegation prompt part.</summary>
internal sealed record OpenCodePromptAgentPart : OpenCodePromptPart
{
    [JsonPropertyName("name")] public required string Name { get; init; }
}

/// <summary>Subtask prompt part.</summary>
internal sealed record OpenCodePromptSubtaskPart : OpenCodePromptPart
{
    [JsonPropertyName("prompt")] public required string Prompt { get; init; }
    [JsonPropertyName("description")] public required string Description { get; init; }
    [JsonPropertyName("agent")] public required string Agent { get; init; }
    [JsonPropertyName("model")] public OpenCodeModelRefRequest? Model { get; init; }
    [JsonPropertyName("command")] public string? Command { get; init; }
}

// ---------------------------------------------------------------------------
// Session Status
// ---------------------------------------------------------------------------

/// <summary>
/// Polymorphic session status (discriminated by "type").
/// Non-abstract so that statuses with an unknown or missing type discriminator
/// fall back to this base type instead of throwing <see cref="System.NotSupportedException"/>.
/// </summary>
[JsonPolymorphic(
    TypeDiscriminatorPropertyName = "type",
    IgnoreUnrecognizedTypeDiscriminators = true,
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FallBackToBaseType)]
[JsonDerivedType(typeof(OpenCodeIdleStatus), "idle")]
[JsonDerivedType(typeof(OpenCodeBusyStatus), "busy")]
[JsonDerivedType(typeof(OpenCodeRetryStatus), "retry")]
internal record OpenCodeSessionStatus;

/// <summary>Session is idle, waiting for a prompt.</summary>
internal sealed record OpenCodeIdleStatus : OpenCodeSessionStatus;

/// <summary>Session is actively processing.</summary>
internal sealed record OpenCodeBusyStatus : OpenCodeSessionStatus
{
    [JsonPropertyName("since")] public long? Since { get; init; }
}

/// <summary>Session is in retry backoff.</summary>
internal sealed record OpenCodeRetryStatus : OpenCodeSessionStatus
{
    [JsonPropertyName("reason")] public string? Reason { get; init; }
    [JsonPropertyName("delay")] public int? Delay { get; init; }
    [JsonPropertyName("count")] public int? Count { get; init; }
}

// ---------------------------------------------------------------------------
// Agents / Providers
// ---------------------------------------------------------------------------

/// <summary>Agent metadata returned by GET /agent.</summary>
internal sealed record OpenCodeAgentInfo
{
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("mode")] public string? Mode { get; init; }
    [JsonPropertyName("hidden")] public bool? Hidden { get; init; }
    [JsonPropertyName("model")] public OpenCodeModelRef? Model { get; init; }
}

/// <summary>AI provider returned by GET /provider.</summary>
internal sealed record OpenCodeProviderInfo
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("models")] public IReadOnlyDictionary<string, OpenCodeProviderModel> Models { get; init; } = new Dictionary<string, OpenCodeProviderModel>();
}

/// <summary>A single model within a provider.</summary>
internal sealed record OpenCodeProviderModel
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("capabilities")] public JsonElement? Capabilities { get; init; }
}

/// <summary>Full providers response from GET /provider.</summary>
internal sealed record OpenCodeProvidersResponse
{
    [JsonPropertyName("all")] public IReadOnlyList<OpenCodeProviderInfo> All { get; init; } = [];
    [JsonPropertyName("default")] public OpenCodeModelRef? Default { get; init; }
    [JsonPropertyName("connected")] public IReadOnlyList<string> Connected { get; init; } = [];
}

// ---------------------------------------------------------------------------
// SSE Events
// ---------------------------------------------------------------------------

/// <summary>A raw server-sent event from the OpenCode event stream.</summary>
internal sealed record OpenCodeSseEvent
{
    [JsonPropertyName("type")] public required string Type { get; init; }
    [JsonPropertyName("properties")] public JsonElement Properties { get; init; }
}

// ---------------------------------------------------------------------------
// Fork
// ---------------------------------------------------------------------------

/// <summary>Request body for POST /session/:id/fork.</summary>
internal sealed record OpenCodeForkRequest
{
    [JsonPropertyName("messageId")] public string? MessageId { get; init; }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.ClaudeCode;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.Infrastructure;

// ── Named types for WebSocketFanOutSubscriber / HarnessEventRelay broadcasts ─────────────────────

internal sealed record ActivityStatusPayload
{
    [JsonPropertyName("sessionId")] public required string SessionId { get; init; }
    [JsonPropertyName("activityStatus")] public required string ActivityStatus { get; init; }
}

// ── Named types for ClaudeCodeMapper (replace anonymous types) ────────────────────────────────────

internal sealed record ClaudeCodeMapperInfoTime
{
    [JsonPropertyName("created")] public long Created { get; init; }
}

internal sealed record ClaudeCodeMapperInfo
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("role")] public required string Role { get; init; }
    [JsonPropertyName("sessionID")] public required string SessionID { get; init; }
    [JsonPropertyName("modelID")] public string? ModelID { get; init; }
    [JsonPropertyName("time")] public required ClaudeCodeMapperInfoTime Time { get; init; }
}

internal sealed record ClaudeCodeMessageUpdatedPayload
{
    [JsonPropertyName("info")] public required ClaudeCodeMapperInfo Info { get; init; }
}

internal sealed record ClaudeCodeTextPartContent
{
    [JsonPropertyName("messageID")] public required string MessageID { get; init; }
    [JsonPropertyName("sessionID")] public required string SessionID { get; init; }
    [JsonPropertyName("type")] public required string Type { get; init; }
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("text")] public required string Text { get; init; }
}

internal sealed record ClaudeCodeTextPartPayload
{
    [JsonPropertyName("part")] public required ClaudeCodeTextPartContent Part { get; init; }
}

internal sealed record ClaudeCodeToolStateContent
{
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("input")] public JsonElement? Input { get; init; }
}

internal sealed record ClaudeCodeToolPartContent
{
    [JsonPropertyName("messageID")] public required string MessageID { get; init; }
    [JsonPropertyName("sessionID")] public required string SessionID { get; init; }
    [JsonPropertyName("type")] public required string Type { get; init; }
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("tool")] public required string Tool { get; init; }
    [JsonPropertyName("callID")] public required string CallID { get; init; }
    [JsonPropertyName("state")] public required ClaudeCodeToolStateContent State { get; init; }
}

internal sealed record ClaudeCodeToolPartPayload
{
    [JsonPropertyName("part")] public required ClaudeCodeToolPartContent Part { get; init; }
}

internal sealed record ClaudeCodeSessionStatusType
{
    [JsonPropertyName("type")] public required string Type { get; init; }
}

internal sealed record ClaudeCodeSessionStatusPayload
{
    [JsonPropertyName("status")] public required ClaudeCodeSessionStatusType Status { get; init; }
}

// ── Session source input types (moved from private nested records in session source providers) ────

/// <summary>Input payload for the repository session source provider.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RepositorySourceInput
{
    [JsonPropertyName("repositoryPath")] public string? RepositoryPath { get; init; }
    [JsonPropertyName("isolationStrategy")] public string? IsolationStrategy { get; init; }
    [JsonPropertyName("branch")] public string? Branch { get; init; }
    [JsonPropertyName("existingWorktreePath")] public string? ExistingWorktreePath { get; init; }
}

/// <summary>Input payload for the GitHub session source provider.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record GitHubSourceInput
{
    [JsonPropertyName("owner")] public string? Owner { get; init; }
    [JsonPropertyName("repo")] public string? Repo { get; init; }
    [JsonPropertyName("number")] public int Number { get; init; }
    [JsonPropertyName("repositoryPath")] public string? RepositoryPath { get; init; }
    [JsonPropertyName("isolationStrategy")] public string? IsolationStrategy { get; init; }
    [JsonPropertyName("branch")] public string? Branch { get; init; }
}

// ── Source-generated contexts ─────────────────────────────────────────────────────────────────────

/// <summary>Default (PascalCase) options for NATS HarnessEvent serialization.
/// Must NOT use CamelCase — stored messages use PascalCase discriminator names.</summary>
[JsonSourceGenerationOptions]
[JsonSerializable(typeof(HarnessEvent))]
internal sealed partial class HarnessEventJsonContext : JsonSerializerContext
{
}

/// <summary>CamelCase + WhenWritingNull options for the OpenCode HTTP API.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(OpenCodeHealthResponse))]
[JsonSerializable(typeof(OpenCodeSessionInfo))]
[JsonSerializable(typeof(List<OpenCodeSessionInfo>))]
[JsonSerializable(typeof(OpenCodeCreateSessionRequest))]
[JsonSerializable(typeof(OpenCodeMessageWithParts))]
[JsonSerializable(typeof(List<OpenCodeMessageWithParts>))]
[JsonSerializable(typeof(OpenCodePromptRequest))]
[JsonSerializable(typeof(OpenCodeForkRequest))]
[JsonSerializable(typeof(OpenCodeCommandRequest))]
[JsonSerializable(typeof(List<OpenCodeAgentInfo>))]
[JsonSerializable(typeof(List<OpenCodeCommandInfo>))]
[JsonSerializable(typeof(OpenCodeProvidersResponse))]
[JsonSerializable(typeof(Dictionary<string, OpenCodeSessionStatus>))]
[JsonSerializable(typeof(OpenCodeSseEvent))]
[JsonSerializable(typeof(OpenCodeTextPart))]
[JsonSerializable(typeof(OpenCodeToolPart))]
[JsonSerializable(typeof(OpenCodeReasoningPart))]
[JsonSerializable(typeof(OpenCodeStepStartPart))]
[JsonSerializable(typeof(OpenCodeStepFinishPart))]
[JsonSerializable(typeof(OpenCodeFilePart))]
[JsonSerializable(typeof(OpenCodeAgentPart))]
[JsonSerializable(typeof(OpenCodeSubtaskPart))]
[JsonSerializable(typeof(OpenCodeSnapshotPart))]
[JsonSerializable(typeof(OpenCodePatchPart))]
[JsonSerializable(typeof(OpenCodeRetryPart))]
[JsonSerializable(typeof(OpenCodeCompactionPart))]
[JsonSerializable(typeof(OpenCodeToolState))]
[JsonSerializable(typeof(OpenCodeToolPending))]
[JsonSerializable(typeof(OpenCodeToolRunning))]
[JsonSerializable(typeof(OpenCodeToolCompleted))]
[JsonSerializable(typeof(OpenCodeToolError))]
[JsonSerializable(typeof(RawToolPart))]
internal sealed partial class OpenCodeJsonContext : JsonSerializerContext
{
}

/// <summary>SnakeCaseLower + WhenWritingNull options for Claude Code NDJSON stream.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ClaudeCodeStreamMessage))]
internal sealed partial class ClaudeCodeJsonContext : JsonSerializerContext
{
}

/// <summary>CamelCase + WhenWritingNull options for Infrastructure-specific payloads.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ActivityStatusPayload))]
[JsonSerializable(typeof(ClaudeCodeMessageUpdatedPayload))]
[JsonSerializable(typeof(ClaudeCodeTextPartPayload))]
[JsonSerializable(typeof(ClaudeCodeToolPartPayload))]
[JsonSerializable(typeof(ClaudeCodeSessionStatusPayload))]
[JsonSerializable(typeof(List<JsonElement>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(RepositorySourceInput))]
[JsonSerializable(typeof(GitHubSourceInput))]
[JsonSerializable(typeof(DeviceCodeResponse))]
internal sealed partial class InfrastructureJsonContext : JsonSerializerContext
{
    /// <summary>Returns a serialized <c>{ "sessionId": "...", "activityStatus": "..." }</c> JsonElement.</summary>
    internal static JsonElement SerializeActivityStatus(string sessionId, string activityStatus)
        => JsonSerializer.SerializeToElement(
            new ActivityStatusPayload { SessionId = sessionId, ActivityStatus = activityStatus },
            Default.ActivityStatusPayload);

    /// <summary>A pre-computed empty JSON object <c>{}</c> as a <see cref="JsonElement"/>.</summary>
    internal static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();
}

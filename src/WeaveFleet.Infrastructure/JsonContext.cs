using System.Text.Json;
using System.Text.Json.Serialization;
using WeaveFleet.Domain.DTOs;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Skills;
using WeaveFleet.Domain.Tools;
using WeaveFleet.Infrastructure.Harnesses.ClaudeCode;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;
using WeaveFleet.Infrastructure.Services;
using WeaveFleet.Infrastructure.Tools;

namespace WeaveFleet.Infrastructure;

// ── Named types for WebSocketFanOutSubscriber / HarnessEventRelay broadcasts ─────────────────────

internal sealed record ActivityStatusPayload
{
    [JsonPropertyName("sessionId")] public required string SessionId { get; init; }
    [JsonPropertyName("activityStatus")] public required string ActivityStatus { get; init; }
    [JsonPropertyName("capabilities")] public required SessionActionCapabilities Capabilities { get; init; }
    [JsonPropertyName("attempt")] public int? Attempt { get; init; }
    [JsonPropertyName("message")] public string? Message { get; init; }
    [JsonPropertyName("next")] public string? Next { get; init; }
}

// ── A harness's own session.status event, the way adapters report a turn starting or ending ──────

internal sealed record SessionStatusEventKind
{
    [JsonPropertyName("type")] public required string Type { get; init; }
}

internal sealed record SessionStatusEventPayload
{
    [JsonPropertyName("status")] public required SessionStatusEventKind Status { get; init; }
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
    [JsonPropertyName("output")] public JsonElement? Output { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
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

// ── Session source input types (moved from private nested records in session source providers) ────

/// <summary>Input payload for the repository session source provider.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RepositorySourceInput
{
    [JsonPropertyName("repositoryPath")] public string? RepositoryPath { get; init; }
    [JsonPropertyName("isolationStrategy")] public string? IsolationStrategy { get; init; }
    [JsonPropertyName("branch")] public string? Branch { get; init; }
    [JsonPropertyName("existingWorktreePath")] public string? ExistingWorktreePath { get; init; }
    [JsonPropertyName("baseBranch")] public string? BaseBranch { get; init; }
    [JsonPropertyName("fetchOrigin")] public bool? FetchOrigin { get; init; }
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
    [JsonPropertyName("existingWorktreePath")] public string? ExistingWorktreePath { get; init; }
    [JsonPropertyName("baseBranch")] public string? BaseBranch { get; init; }
    [JsonPropertyName("fetchOrigin")] public bool? FetchOrigin { get; init; }
}

/// <summary>Input payload for the automation session source provider.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AutomationSourceInput
{
    [JsonPropertyName("automationId")] public string? AutomationId { get; init; }
    [JsonPropertyName("automationName")] public string? AutomationName { get; init; }
    [JsonPropertyName("trigger")] public string? Trigger { get; init; }
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
[JsonSerializable(typeof(OpenCodeSessionIdentity))]
[JsonSerializable(typeof(OpenCodeSessionParent))]
[JsonSerializable(typeof(OpenCodeSessionInfo))]
[JsonSerializable(typeof(List<OpenCodeSessionInfo>))]
[JsonSerializable(typeof(OpenCodeCreateSessionRequest))]
[JsonSerializable(typeof(OpenCodeSessionUpdateRequest))]
[JsonSerializable(typeof(OpenCodeMessageWithParts))]
[JsonSerializable(typeof(List<OpenCodeMessageWithParts>))]
[JsonSerializable(typeof(OpenCodePromptRequest))]
[JsonSerializable(typeof(OpenCodeForkRequest))]
[JsonSerializable(typeof(OpenCodeCommandRequest))]
[JsonSerializable(typeof(List<OpenCodeAgentInfo>))]
[JsonSerializable(typeof(List<OpenCodeCommandInfo>))]
[JsonSerializable(typeof(OpenCodeProvidersResponse))]
[JsonSerializable(typeof(OpenCodeConfigDefaults))]
[JsonSerializable(typeof(Dictionary<string, OpenCodeModelVariant>))]
[JsonSerializable(typeof(Dictionary<string, OpenCodeSessionStatus>))]
[JsonSerializable(typeof(OpenCodeSseEvent))]
[JsonSerializable(typeof(List<OpenCodeTodo>))]
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
[JsonSerializable(typeof(OpenCodeToolResultPart))]
[JsonSerializable(typeof(OpenCodeToolState))]
[JsonSerializable(typeof(OpenCodeToolPending))]
[JsonSerializable(typeof(OpenCodeToolRunning))]
[JsonSerializable(typeof(OpenCodeToolCompleted))]
[JsonSerializable(typeof(OpenCodeToolError))]
[JsonSerializable(typeof(RawToolPart))]
[JsonSerializable(typeof(OpenCodeQuestionReplyRequest))]
[JsonSerializable(typeof(OpenCodeQuestionRejectRequest))]
[JsonSerializable(typeof(OpenCodePermissionReplyRequest))]
internal sealed partial class OpenCodeJsonContext : JsonSerializerContext
{
}

/// <summary>
/// SnakeCaseLower + WhenWritingNull options for Claude Code NDJSON stream. Claude Code doesn't always
/// write "type" first (result lines, tool results), so the discriminator is looked for anywhere.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    AllowOutOfOrderMetadataProperties = true)]
[JsonSerializable(typeof(ClaudeCodeStreamMessage))]
internal sealed partial class ClaudeCodeJsonContext : JsonSerializerContext
{
}

/// <summary>CamelCase + WhenWritingNull options for Infrastructure-specific payloads.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ActivityStatusPayload))]
[JsonSerializable(typeof(SessionActionCapabilities))]
[JsonSerializable(typeof(ClaudeCodeMessageUpdatedPayload))]
[JsonSerializable(typeof(ClaudeCodeTextPartPayload))]
[JsonSerializable(typeof(ClaudeCodeToolPartPayload))]
[JsonSerializable(typeof(SessionStatusEventPayload))]
[JsonSerializable(typeof(MessageLifecyclePayload))]
[JsonSerializable(typeof(MessagePartUpdatedPayload))]
[JsonSerializable(typeof(MessagePartDeltaStreamedPayload))]
[JsonSerializable(typeof(DelegationCreatedPayload))]
[JsonSerializable(typeof(DelegationUpdatedPayload))]
[JsonSerializable(typeof(DelegationCompletedPayload))]
[JsonSerializable(typeof(SessionStartedPayload))]
[JsonSerializable(typeof(SessionDeletedPayload))]
[JsonSerializable(typeof(FilesChangedPayload))]
[JsonSerializable(typeof(FileChangeEntry))]
[JsonSerializable(typeof(List<FileChangeEntry>))]
[JsonSerializable(typeof(TodosReportedPayload))]
[JsonSerializable(typeof(TodoEntry))]
[JsonSerializable(typeof(List<TodoEntry>))]
[JsonSerializable(typeof(FilesWrittenPayload))]
[JsonSerializable(typeof(WeaveFleet.Infrastructure.Data.Repositories.SessionProgressDetailJson))]
[JsonSerializable(typeof(WeaveFleet.Domain.Entities.TrackedPlan))]
[JsonSerializable(typeof(List<WeaveFleet.Domain.Entities.TrackedPlan>))]
[JsonSerializable(typeof(WeaveFleet.Application.DTOs.SessionProgressSummaryDto))]
[JsonSerializable(typeof(WeaveFleet.Application.DTOs.SessionProgressDto))]
[JsonSerializable(typeof(List<JsonElement>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(RepositorySourceInput))]
[JsonSerializable(typeof(GitHubSourceInput))]
[JsonSerializable(typeof(AutomationSourceInput))]
[JsonSerializable(typeof(DeviceCodeResponse))]
[JsonSerializable(typeof(CachedCatalog))]
[JsonSerializable(typeof(CatalogEntry))]
[JsonSerializable(typeof(List<CatalogEntry>))]
[JsonSerializable(typeof(SkillManifest))]
[JsonSerializable(typeof(SkillManifestEntry))]
[JsonSerializable(typeof(SkillSource))]
[JsonSerializable(typeof(CachedToolCatalog))]
[JsonSerializable(typeof(ToolCatalogEntry))]
[JsonSerializable(typeof(List<ToolCatalogEntry>))]
[JsonSerializable(typeof(ToolManifest))]
[JsonSerializable(typeof(ToolManifestEntry))]
[JsonSerializable(typeof(ToolType))]
[JsonSerializable(typeof(WeaveFleet.Application.DTOs.SmartLinkDto))]
internal sealed partial class InfrastructureJsonContext : JsonSerializerContext
{
    /// <summary>Returns a serialized activity-status payload.</summary>
    internal static JsonElement SerializeActivityStatus(
        string sessionId,
        string activityStatus,
        SessionActionCapabilities capabilities,
        int? retryAttempt = null,
        string? retryMessage = null,
        DateTimeOffset? retryNext = null)
        => JsonSerializer.SerializeToElement(
            new ActivityStatusPayload
            {
                SessionId = sessionId,
                ActivityStatus = activityStatus,
                Capabilities = capabilities,
                Attempt = retryAttempt,
                Message = retryMessage,
                Next = retryNext?.ToString("O")
            },
            Default.ActivityStatusPayload);

    /// <summary>A pre-computed empty JSON object <c>{}</c> as a <see cref="JsonElement"/>.</summary>
    internal static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();
}

// ── Skill catalog cache ───────────────────────────────────────────────────────────────────────────

/// <summary>Cached skill catalog with timestamp.</summary>
internal sealed record CachedCatalog(IReadOnlyList<CatalogEntry> Entries, DateTimeOffset CachedAt);

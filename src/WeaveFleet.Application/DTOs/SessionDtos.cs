using WeaveFleet.Domain.DTOs;

namespace WeaveFleet.Application.DTOs;

/// <summary>
/// Response DTO for a session list item — matches the SessionListItem shape from the frontend api-types.ts.
/// </summary>
public sealed record SessionListResponse(
    string InstanceId,
    string WorkspaceId,
    string WorkspaceDirectory,
    string? WorkspaceDisplayName,
    string IsolationStrategy,
    string SessionStatus,
    SessionFleetInfo Session,
    string InstanceStatus,
    string? ParentSessionId,
    string? SourceDirectory,
    string? Branch,
    string? ActivityStatus,
    string LifecycleStatus,
    string RetentionStatus,
    string? ArchivedAt,
    string TypedInstanceStatus,
    bool IsHidden,
    int? TotalTokens,
    double? TotalCost,
    string? ProjectId,
    string? ProjectName,
    string HarnessType,
    SessionActionCapabilities Capabilities,
    List<string> Tags)
{
    public SessionOriginDto? Origin { get; init; }

    /// <summary>How far along the session is, when Fleet has seen a todo list or plan for it.</summary>
    public SessionProgressSummaryDto? Progress { get; init; }

    /// <summary>The agent a prompt that names none goes to; null for the harness's default.</summary>
    public string? SelectedAgent { get; init; }

    /// <summary>The model a prompt that names none gets; null for the agent's or the harness's default.</summary>
    public SessionModelChoiceDto? SelectedModel { get; init; }

    /// <summary>The workflow run this session is a step of; the session list nests it under the run.</summary>
    public string? WorkflowRunId { get; init; }

    /// <summary>While the harness waits to retry a failed model call: which attempt this is, out of how many.</summary>
    public int? RetryAttempt { get; init; }
    public int? RetryMaxAttempts { get; init; }

    /// <summary>While the harness waits to retry: why the call failed, and when it tries again (ISO 8601).</summary>
    public string? RetryMessage { get; init; }
    public string? RetryNext { get; init; }

    /// <summary>How much work the session's agent left running (subagents, background shells, monitors): the list's chip.</summary>
    public int RunningWorkCount { get; init; }

    /// <summary>The session this one is a fork of, or null.</summary>
    public string? ForkedFromSessionId { get; init; }

    /// <summary>The session whose agent started this one, or null.</summary>
    public string? SpawnedBySessionId { get; init; }

    /// <summary>How the session came to be: <c>fork</c>, <c>api</c>, <c>message</c>, <c>automation</c> or <c>workflow</c>; null when the user started it.</summary>
    public string? SpawnKind { get; init; }

    /// <summary>When the user moved it out of the session it came from, or null; it no longer nests under that one.</summary>
    public string? LineageDetachedAt { get; init; }

    /// <summary>Where it sits in the Pinned group above the projects (ascending), or null when it isn't pinned.</summary>
    public double? PinOrder { get; init; }

    /// <summary>When Fleet tries a turn a model provider's limit stopped again, or null: the row says so.</summary>
    public WeaveFleet.Application.Services.ScheduledRetryView? ScheduledRetry { get; init; }
}

/// <summary>A model as the harness names one.</summary>
public sealed record SessionModelChoiceDto(string ProviderID, string ModelID)
{
    public static SessionModelChoiceDto? Of(string? providerId, string? modelId) =>
        string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(modelId) ? null : new(providerId, modelId);
}

/// <summary>
/// Lightweight origin metadata for a session.
/// </summary>
public sealed record SessionOriginDto(
    string SourceType,
    string? Title,
    string? ResourceUrl,
    string? ResourceId,
    string ProviderId);

/// <summary>
/// The nested session object within SessionListResponse — matches the FleetSession shape.
/// </summary>
public sealed record SessionFleetInfo(
    string Id,
    string Title,
    SessionTime Time,
    List<string> Tags);

/// <summary>Timestamps for a session (Unix ms).</summary>
public sealed record SessionTime(long Created, long Updated);

/// <summary>Request DTO for moving a session to a different project.</summary>
public sealed record MoveSessionRequest(string? ProjectId);

/// <summary>Moves a fork or a session an agent started out of the session it came from (true), or back under it (false).</summary>
public sealed record UpdateSessionLineageRequest(bool Detached);

/// <summary>Pins a session just before another pinned one, or at the end of the Pinned group when that's null.</summary>
public sealed record PinSessionRequest(string? BeforeSessionId);

/// <summary>Where a session now sits in the Pinned group (ascending).</summary>
public sealed record PinSessionResponse(double PinOrder);

/// <summary>Request DTO for renaming a session.</summary>
public sealed record UpdateSessionTitleRequest(string Title);

/// <summary>Request DTO for changing a session's retention state.</summary>
public sealed record UpdateSessionRetentionRequest(string RetentionStatus);

/// <summary>
/// Response DTO for accepted prompt submissions. <paramref name="MessageId"/> is the id the harness was given for the
/// prompt, which its replies name as their parent.
/// </summary>
public sealed record PromptSessionResult(long? EventId, string CorrelationId, string? MessageId = null);

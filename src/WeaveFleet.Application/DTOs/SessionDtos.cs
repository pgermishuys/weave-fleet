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
    string? HarnessType,
    SessionActionCapabilities Capabilities)
{
    public SessionOriginDto? Origin { get; init; }
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
    SessionTime Time);

/// <summary>Timestamps for a session (Unix ms).</summary>
public sealed record SessionTime(long Created, long Updated);

/// <summary>Request DTO for moving a session to a different project.</summary>
public sealed record MoveSessionRequest(string? ProjectId);

/// <summary>Request DTO for renaming a session.</summary>
public sealed record UpdateSessionTitleRequest(string Title);

/// <summary>Request DTO for changing a session's retention state.</summary>
public sealed record UpdateSessionRetentionRequest(string RetentionStatus);

/// <summary>Response DTO for accepted prompt submissions.</summary>
public sealed record PromptSessionResult(long? EventId, string CorrelationId);

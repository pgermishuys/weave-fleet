using System.Collections.Immutable;

namespace NuCode.Sessions;

/// <summary>
/// Represents a conversation session. Sessions contain messages and parts, 
/// and may have parent-child relationships (for forked or subagent sessions).
/// </summary>
public sealed record NuCodeSession
{
    /// <summary>Unique session identifier.</summary>
    public required SessionId Id { get; init; }

    /// <summary>Human-friendly slug for URL-safe references.</summary>
    public required string Slug { get; init; }

    /// <summary>Working directory for this session.</summary>
    public required string Directory { get; init; }

    /// <summary>Session title (auto-generated or user-provided).</summary>
    public required string Title { get; init; }

    /// <summary>Library version that created this session.</summary>
    public required string Version { get; init; }

    /// <summary>Parent session ID for forked or subagent sessions.</summary>
    public SessionId? ParentId { get; init; }

    /// <summary>Session creation timestamp.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Last update timestamp.</summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>When the session was archived (null if active).</summary>
    public DateTimeOffset? ArchivedAt { get; init; }

    /// <summary>When context compaction started (null if not compacting).</summary>
    public DateTimeOffset? CompactingAt { get; init; }

    /// <summary>Session-level permission ruleset.</summary>
    public Permissions.PermissionRuleset? Permissions { get; init; }

    /// <summary>Change summary for the session (file diffs, additions/deletions).</summary>
    public SessionSummary? Summary { get; init; }

    /// <summary>Share URL if the session has been shared.</summary>
    public string? ShareUrl { get; init; }

    /// <summary>Revert point information.</summary>
    public SessionRevert? Revert { get; init; }
}

/// <summary>
/// Summary of changes made during a session.
/// </summary>
public sealed record SessionSummary(
    int Additions,
    int Deletions,
    int Files,
    ImmutableArray<FileDiff>? Diffs = null);

/// <summary>
/// A file diff entry in a session summary.
/// </summary>
public sealed record FileDiff(string Path, string Diff);

/// <summary>
/// Information about a session revert point.
/// </summary>
public sealed record SessionRevert(
    MessageId MessageId,
    PartId? PartId = null,
    string? Snapshot = null,
    string? Diff = null);

/// <summary>
/// Tracks the processing status of a session.
/// </summary>
public abstract record SessionStatus(string Type);

/// <summary>Session is idle, not processing.</summary>
public sealed record IdleSessionStatus() : SessionStatus("idle");

/// <summary>Session is actively processing a request.</summary>
public sealed record BusySessionStatus() : SessionStatus("busy");

/// <summary>Session is retrying after an error.</summary>
public sealed record RetrySessionStatus(
    int Attempt,
    string Message,
    DateTimeOffset NextRetryAt)
    : SessionStatus("retry");

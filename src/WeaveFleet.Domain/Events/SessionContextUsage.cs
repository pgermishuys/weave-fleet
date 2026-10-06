using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Domain.Events;

/// <summary>
/// How full a session's context window is, as clients see it: in the session's snapshot, from
/// <c>GET /api/sessions/{id}/context</c>, and live as a <c>context.updated</c> event on the session's topic.
/// </summary>
public sealed record SessionContextUsage
{
    /// <summary>The event clients get on <c>session:{id}</c> whenever it changes, with this as its payload.</summary>
    public const string UpdatedEventType = "context.updated";

    public required string SessionId { get; init; }

    /// <summary>Tokens in the context as of the last model call; null before the first and right after a compaction.</summary>
    public int? Used { get; init; }

    /// <summary>The model's context window in tokens; null when the harness hasn't said.</summary>
    public int? Limit { get; init; }

    /// <summary>How many tokens make the harness compact on its own, when it can tell.</summary>
    public int? CompactsAt { get; init; }

    public string? ModelId { get; init; }
    public string? ProviderId { get; init; }

    /// <summary>The last model call's tokens.</summary>
    public ContextCall? LastCall { get; init; }

    public DateTimeOffset? LastCallAt { get; init; }

    /// <summary>A compaction is under way.</summary>
    public bool Compacting { get; init; }

    public DateTimeOffset? CompactedAt { get; init; }

    /// <summary>Why the last compaction failed, until another one starts.</summary>
    public string? CompactionError { get; init; }

    /// <summary>The context's size at the end of each recent turn, oldest first.</summary>
    public IReadOnlyList<SessionContextTurn> Turns { get; init; } = [];

    public DateTimeOffset UpdatedAt { get; init; }

    public static SessionContextUsage From(SessionContext context) => new()
    {
        SessionId = context.SessionId,
        Used = context.Used,
        Limit = context.Limit,
        CompactsAt = context.CompactsAt,
        ModelId = context.ModelId,
        ProviderId = context.ProviderId,
        LastCall = context.LastCall,
        LastCallAt = context.LastCallAt,
        Compacting = context.Compacting,
        CompactedAt = context.CompactedAt,
        CompactionError = context.CompactionError,
        Turns = context.Turns,
        UpdatedAt = context.UpdatedAt,
    };
}

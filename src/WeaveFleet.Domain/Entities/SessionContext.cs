using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Domain.Entities;

/// <summary>
/// How full a session's context window is, as its harness last reported it (<see cref="ContextUsageReport"/>,
/// <see cref="ContextCompactionReport"/>), with the size it had at the end of each recent turn.
/// </summary>
public sealed record SessionContext
{
    /// <summary>The most turns kept in <see cref="Turns"/>; older ones are dropped.</summary>
    public const int MaxTurns = 40;

    public required string SessionId { get; init; }
    public required string UserId { get; init; }

    /// <summary>
    /// Tokens in the context as of the last model call (<see cref="ContextCall.Used"/>). Null before the first call
    /// and after a compaction, until the next call measures the context again.
    /// </summary>
    public int? Used { get; init; }

    /// <summary>The model's context window in tokens, when the harness knows it.</summary>
    public int? Limit { get; init; }

    /// <summary>How many tokens make the harness compact on its own, when it can tell.</summary>
    public int? CompactsAt { get; init; }

    public string? ModelId { get; init; }
    public string? ProviderId { get; init; }

    /// <summary>The last model call's tokens; null where <see cref="Used"/> is.</summary>
    public ContextCall? LastCall { get; init; }

    /// <summary>When the last call was reported.</summary>
    public DateTimeOffset? LastCallAt { get; init; }

    /// <summary>A compaction is under way.</summary>
    public bool Compacting { get; init; }

    /// <summary>When the context was last compacted.</summary>
    public DateTimeOffset? CompactedAt { get; init; }

    /// <summary>Why the last compaction failed; cleared by the next one that starts.</summary>
    public string? CompactionError { get; init; }

    /// <summary>The context's size at the end of each recent turn, oldest first, at most <see cref="MaxTurns"/>.</summary>
    public IReadOnlyList<SessionContextTurn> Turns { get; init; } = [];

    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>A session's context at the end of one turn.</summary>
/// <param name="Used">Tokens in the context after the turn's last model call.</param>
/// <param name="Limit">The model's context window then, when known.</param>
/// <param name="At">When the turn ended.</param>
/// <param name="AfterCompaction">The context was compacted during or before this turn, since the turn before it.</param>
public sealed record SessionContextTurn(int Used, int? Limit, DateTimeOffset At, bool AfterCompaction);

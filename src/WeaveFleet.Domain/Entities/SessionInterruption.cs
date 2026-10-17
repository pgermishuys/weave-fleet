namespace WeaveFleet.Domain.Entities;

/// <summary>
/// A session whose turn a Fleet restart cut off. One per session, kept in Fleet's database until the user continues
/// it or dismisses it.
/// </summary>
public sealed record SessionInterruption
{
    public required string SessionId { get; init; }

    /// <summary>The session's owner.</summary>
    public required string UserId { get; init; }

    /// <summary>When the turn started: the last moment it was known to be working.</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>The turn was waiting on a question or permission, which died with the harness.</summary>
    public bool Waiting { get; init; }

    /// <summary>The tool call that hadn't finished, as a short label, when history shows one.</summary>
    public string? LastCall { get; init; }

    /// <summary>How many files differ in the session's folder, when Fleet could read it.</summary>
    public int? ChangedFiles { get; init; }
}

/// <summary>A session's turn as the last Fleet left it.</summary>
public sealed record FlaggedTurn(string SessionId, string UserId, DateTimeOffset StartedAt, bool Waiting);

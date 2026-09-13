using WeaveFleet.Domain.Events;

namespace WeaveFleet.Domain.Entities;

/// <summary>
/// How far along a session is, as Fleet last worked it out from the session's events.
/// </summary>
public sealed record SessionProgress
{
    public required string SessionId { get; init; }
    public required string UserId { get; init; }

    /// <summary>What the counts are counting, one of <see cref="SessionProgressKinds"/>.</summary>
    public required string Kind { get; init; }

    public int Done { get; init; }

    /// <summary>Items that count towards progress. Cancelled todos don't.</summary>
    public int Total { get; init; }

    /// <summary>The item being worked on, or the next one when nothing is in progress.</summary>
    public string? Current { get; init; }

    /// <summary>The agent's todo list as it last wrote it.</summary>
    public IReadOnlyList<TodoEntry> Todos { get; init; } = [];

    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>What a session's progress counts.</summary>
public static class SessionProgressKinds
{
    /// <summary>The agent's todo list.</summary>
    public const string Todos = "todos";
}

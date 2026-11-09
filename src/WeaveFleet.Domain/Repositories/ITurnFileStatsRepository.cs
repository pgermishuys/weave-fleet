namespace WeaveFleet.Domain.Repositories;

/// <summary>A file's change against the session's baseline: path as the session's folder knows it, lines added and removed.</summary>
public sealed record TurnFileStat(string File, int Additions, int Deletions);

/// <summary>The session's changed files as one turn ended.</summary>
public sealed record TurnFileStats(DateTimeOffset EndedAt, IReadOnlyList<TurnFileStat> Files);

/// <summary>
/// The changed files of the session as each turn ended (<c>turn_file_stats</c>, migration 073). Both calls are scoped to
/// the current user's sessions; <see cref="AddAsync"/> is made as the session's owner by the recorder.
/// </summary>
public interface ITurnFileStatsRepository
{
    /// <summary>Keeps the session's changed files as a turn ended, and drops all but the newest snapshots.</summary>
    Task AddAsync(string sessionId, DateTimeOffset endedAt, IReadOnlyList<TurnFileStat> files);

    /// <summary>The session's snapshots, oldest first.</summary>
    Task<IReadOnlyList<TurnFileStats>> ListAsync(string sessionId);
}

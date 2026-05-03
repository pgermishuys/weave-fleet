namespace NuCode.Sessions;

/// <summary>
/// Service responsible for detecting when conversation compaction is needed
/// and performing the compaction by summarizing older messages.
/// </summary>
internal interface ICompactionService
{
    /// <summary>
    /// Checks whether the session has exceeded compaction thresholds.
    /// </summary>
    Task<bool> NeedsCompactionAsync(SessionId sessionId, CancellationToken ct);

    /// <summary>
    /// Compacts older messages in the session by summarizing them.
    /// </summary>
    /// <param name="sessionId">The session to compact.</param>
    /// <param name="overflow">True if triggered by a context overflow error.</param>
    /// <param name="ct">Cancellation token.</param>
    Task CompactAsync(SessionId sessionId, bool overflow, CancellationToken ct);
}

namespace NuCode.Sessions;

/// <summary>
/// Convenience extension methods for <see cref="ISessionStore"/> to avoid passing null for optional parameters.
/// </summary>
public static class SessionStoreExtensions
{
    /// <summary>Gets all messages for a session (no limit).</summary>
    public static Task<IReadOnlyList<MessageWithParts>> GetMessagesAsync(
        this ISessionStore store,
        SessionId sessionId,
        CancellationToken ct)
        => store.GetMessagesAsync(sessionId, null, ct);
}

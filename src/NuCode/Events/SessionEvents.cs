namespace NuCode.Events;

/// <summary>
/// Session lifecycle events.
/// </summary>
public static class SessionEvents
{
    /// <summary>Properties for session lifecycle events.</summary>
    public sealed record SessionInfo(SessionId SessionId, string? Title);

    /// <summary>A new session was created.</summary>
    public static readonly NuCodeEventDefinition<SessionInfo> Created = new("session.created");

    /// <summary>A session was updated (e.g., title changed).</summary>
    public static readonly NuCodeEventDefinition<SessionInfo> Updated = new("session.updated");

    /// <summary>A session was deleted.</summary>
    public static readonly NuCodeEventDefinition<SessionInfo> Deleted = new("session.deleted");

    /// <summary>Properties for session error events.</summary>
    public sealed record SessionError(SessionId? SessionId, string Error);

    /// <summary>A session encountered an error.</summary>
    public static readonly NuCodeEventDefinition<SessionError> Error = new("session.error");
}

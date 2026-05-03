namespace NuCode.Sessions;

/// <summary>
/// Provides ambient access to the current session ID via <see cref="AsyncLocal{T}"/>.
/// Set by <see cref="SessionProcessor"/> before invoking an agent so that tools
/// (e.g., <see cref="Tools.TaskTool"/>) can discover the parent session without
/// requiring it as a parameter.
/// </summary>
internal static class SessionContext
{
    private static readonly AsyncLocal<SessionId?> CurrentSessionId = new();

    /// <summary>
    /// Gets the session ID for the currently executing session, or <c>null</c>
    /// if no session is active.
    /// </summary>
    public static SessionId? Current => CurrentSessionId.Value;

    /// <summary>
    /// Sets the current session ID. Should be called in a try/finally block
    /// with <see cref="Clear"/> in the finally.
    /// </summary>
    public static void Set(SessionId id) => CurrentSessionId.Value = id;

    /// <summary>
    /// Clears the current session ID.
    /// </summary>
    public static void Clear() => CurrentSessionId.Value = null;
}

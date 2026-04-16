namespace WeaveFleet.Domain.Harnesses;

/// <summary>String constants for all known harness event types.</summary>
public static class EventTypes
{
    public const string MessageCreated = "message.created";
    public const string MessageUpdated = "message.updated";
    public const string MessagePartUpdated = "message.part.updated";
    public const string MessagePartDelta = "message.part.delta";
    public const string MessageRemoved = "message.removed";
    public const string MessagePartRemoved = "message.part.removed";

    public const string SessionUpdated = "session.updated";
    public const string SessionError = "session.error";
    public const string SessionCompacted = "session.compacted";
    public const string SessionDeleted = "session.deleted";
    public const string SessionStatus = "session.status";
    public const string SessionIdle = "session.idle";

    public const string Error = "error";

    public const string ServerHeartbeat = "server.heartbeat";
    public const string ServerConnected = "server.connected";

    /// <summary>Returns <c>true</c> if the event type is a permission event (i.e. starts with "permission.").</summary>
    public static bool IsPermissionEvent(string type) =>
        type.StartsWith("permission.", StringComparison.Ordinal);
}

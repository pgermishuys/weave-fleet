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
    public const string UserPromptCommitted = "user.prompt.committed";

    public const string SessionCreated = "session.created";
    public const string SessionUpdated = "session.updated";
    public const string SessionError = "session.error";
    public const string SessionCompacted = "session.compacted";
    public const string SessionDeleted = "session.deleted";
    public const string SessionStatus = "session.status";
    public const string SessionIdle = "session.idle";
    public const string SessionDiff = "session.diff";

    public const string Error = "error";

    public const string ServerHeartbeat = "server.heartbeat";
    public const string ServerConnected = "server.connected";

    public const string FileWatcherUpdated = "file.watcher.updated";

    /// <summary>
    /// The agent's todo list changed. Fleet's own event: each harness adapter maps its harness's todo updates
    /// onto it, with a <c>TodosReportedPayload</c> as the payload.
    /// </summary>
    public const string TodosReported = "todos.reported";

    /// <summary>
    /// The agent finished writing files. Fleet's own event: each harness adapter reports its file-writing tool
    /// calls with a <c>FilesWrittenPayload</c>, once per call.
    /// </summary>
    public const string FilesWritten = "files.written";

    /// <summary>Returns <c>true</c> if the event type is a permission event (i.e. starts with "permission.").</summary>
    public static bool IsPermissionEvent(string type) =>
        type.StartsWith("permission.", StringComparison.Ordinal);

    /// <summary>Returns <c>true</c> if the event type is a file watcher event (i.e. starts with "file.watcher.").</summary>
    public static bool IsFileWatcherEvent(string type) =>
        type.StartsWith("file.watcher.", StringComparison.Ordinal);
}

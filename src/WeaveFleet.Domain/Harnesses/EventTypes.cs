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

    /// <summary>
    /// The agent asks to do something the session's permission level doesn't allow. Fleet's own event: each adapter
    /// turns its harness's ask into a <see cref="PermissionAsk"/> payload; the harness's own permission events stay in
    /// the adapter.
    /// </summary>
    public const string PermissionAsked = "permission.asked";

    /// <summary>An ask was answered, or went away with its harness. Fleet's own event, with a <see cref="Harnesses.PermissionReplied"/> payload.</summary>
    public const string PermissionReplied = "permission.replied";

    /// <summary>
    /// The agent left work running: a subagent, a background shell, a monitor. Fleet's own event, with a
    /// <see cref="WorkReport"/> payload. The relay hands it to Fleet's running-work record rather than the conversation.
    /// </summary>
    public const string WorkStarted = "work.started";

    /// <summary>Something about running work changed (its child session, it went to the background). A <see cref="WorkReport"/> with what changed.</summary>
    public const string WorkUpdated = "work.updated";

    /// <summary>Running work ended. A <see cref="WorkReport"/> with <see cref="WorkReport.EndedReason"/>.</summary>
    public const string WorkEnded = "work.ended";

    /// <summary>
    /// The size of the session's last model call and its model's limits. Fleet's own event, with a
    /// <see cref="ContextUsageReport"/> payload. The relay hands it to Fleet's record of the session's context rather
    /// than the conversation.
    /// </summary>
    public const string ContextUsage = "context.usage";

    /// <summary>The session's context was compacted, or a compaction started or failed. A <see cref="ContextCompactionReport"/>.</summary>
    public const string ContextCompaction = "context.compaction";

    /// <summary>
    /// How much of its account's usage limits the harness has used (a <see cref="UsageLimitReport"/>). The account's, not
    /// the session's: the relay hands it to Fleet's record of each harness's limits rather than the conversation.
    /// </summary>
    public const string HarnessUsage = "harness.usage";

    /// <summary>Returns <c>true</c> for <see cref="ContextUsage"/> and <see cref="ContextCompaction"/>.</summary>
    public static bool IsContextEvent(string type) => type is ContextUsage or ContextCompaction;

    /// <summary>Returns <c>true</c> for <see cref="WorkStarted"/>, <see cref="WorkUpdated"/> and <see cref="WorkEnded"/>.</summary>
    public static bool IsWorkEvent(string type) => type is WorkStarted or WorkUpdated or WorkEnded;

    /// <summary>Returns <c>true</c> if the event type is a permission event (i.e. starts with "permission.").</summary>
    public static bool IsPermissionEvent(string type) =>
        type.StartsWith("permission.", StringComparison.Ordinal);

    /// <summary>Returns <c>true</c> if the event type is a file watcher event (i.e. starts with "file.watcher.").</summary>
    public static bool IsFileWatcherEvent(string type) =>
        type.StartsWith("file.watcher.", StringComparison.Ordinal);
}

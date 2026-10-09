using System.Text.Json;

namespace WeaveFleet.Domain.Harnesses;

/// <summary>
/// A running bridge to an AI agent, one per session.
/// Implementations are created by <c>IHarnessRuntime.SpawnAsync</c>.
/// </summary>
public interface IHarnessSession : IAsyncDisposable
{
    /// <summary>Unique identifier for this running instance.</summary>
    string InstanceId { get; }

    /// <summary>OS process ID of the underlying agent process, if available.</summary>
    int? ProcessId { get; }

    /// <summary>
    /// Opaque token used to resume this session after a crash or restart.
    /// Null if the harness has not yet captured a session ID or does not support resume.
    /// </summary>
    string? ResumeToken { get; }

    /// <summary>The harness type that created this instance (e.g. "opencode").</summary>
    string HarnessType { get; }

    /// <summary>Current lifecycle status.</summary>
    HarnessSessionStatus Status { get; }

    /// <summary>Gracefully stop the agent process.</summary>
    Task StopAsync(CancellationToken ct);

    /// <summary>Permanently purge remote session state, then stop the agent process.</summary>
    Task DeleteAsync(CancellationToken ct);

    /// <summary>
    /// The session was archived: ends what the agent started and left running (a server it started from its shell), as
    /// archiving ends the session's terminals and apps. The session wakes on its next prompt, as after an idle stop. A
    /// harness that doesn't support it does nothing.
    /// </summary>
    Task ArchiveAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>Send a user prompt to the agent.</summary>
    Task SendPromptAsync(string text, PromptOptions? options, CancellationToken ct);

    /// <summary>
    /// Ask a one-shot question that sees the whole conversation but leaves no trace in it: nothing is
    /// added to the session's history and no tools run. Returns the answer's text, or null when the
    /// harness can't do this or the session has no prompt to answer from yet. Used for session recaps.
    /// </summary>
    Task<string?> AskOffTheRecordAsync(string prompt, CancellationToken ct);

    /// <summary>
    /// Starts a conversation off the record: it sees the whole session, as <see cref="AskOffTheRecordAsync"/> does,
    /// and a follow-up question sees the earlier questions and answers. Nothing reaches the session's history; disposing
    /// the conversation deletes whatever the harness made for it. Null when the harness can't, or the session has no
    /// prompt to answer from yet. Used to draft a workflow from a session.
    /// </summary>
    Task<IOffTheRecordConversation?> StartOffTheRecordAsync(CancellationToken ct)
        => Task.FromResult<IOffTheRecordConversation?>(null);

    /// <summary>
    /// Execute a slash command on the agent. Returns the id of the user message the command put in the conversation:
    /// <see cref="CommandOptions.MessageId"/> when the harness stored it under that, the harness's own id when it
    /// didn't, or null when it put none there Fleet can name (a command that runs as a subagent).
    /// </summary>
    Task<string?> SendCommandAsync(CommandOptions options, CancellationToken ct);

    /// <summary>
    /// Runs a shell command the user typed in the session's folder, without a model turn: the command and its output
    /// go into the conversation (<see cref="ShellCommands"/>), where the agent sees them on its next turn. Returns
    /// once the harness has taken the command; it may still be running. Throws <see cref="HarnessBusyException"/>
    /// when the harness won't run one during a turn. Only for a harness with
    /// <see cref="HarnessCapabilities.SupportsShellCommands"/>.
    /// </summary>
    Task RunShellCommandAsync(ShellCommandOptions options, CancellationToken ct)
        => throw new NotSupportedException($"{HarnessType} sessions can't run shell commands.");

    /// <summary>
    /// Compacts the session's context: the harness summarises the conversation so far, and the agent carries on from
    /// the summary. Returns once the harness has taken the request; the compaction's progress and end arrive as
    /// <see cref="EventTypes.ContextCompaction"/> events. Throws <see cref="HarnessBusyException"/> when the harness
    /// won't compact during a turn. Only for a harness with <see cref="HarnessCapabilities.SupportsCompaction"/>.
    /// </summary>
    Task CompactAsync(CompactOptions options, CancellationToken ct)
        => throw new NotSupportedException($"{HarnessType} sessions can't be compacted.");

    /// <summary>
    /// Copies the conversation up to its last finished turn into a new harness session, for Fork and for a side
    /// conversation (<c>/btw</c>). A turn still running isn't copied: the fork would carry on with it. The session
    /// itself is left as it is. Null when the harness can't. Only for a harness with
    /// <see cref="HarnessCapabilities.SupportsForking"/>.
    /// </summary>
    Task<ConversationFork?> ForkConversationAsync(CancellationToken ct)
        => Task.FromResult<ConversationFork?>(null);

    /// <summary>Abort the current agent operation.</summary>
    Task AbortAsync(CancellationToken ct);

    /// <summary>Answer a pending question request from the agent.</summary>
    Task AnswerQuestionAsync(string requestId, IReadOnlyList<IReadOnlyList<string>> answers, CancellationToken ct);

    /// <summary>Reject (dismiss) a pending question request from the agent.</summary>
    Task RejectQuestionAsync(string requestId, CancellationToken ct);

    /// <summary>
    /// Sets what the agent may do without asking (Settings → Permissions). Fleet calls it before every prompt and when
    /// the session starts, so a changed setting applies from the session's next message. An ask the policy doesn't
    /// allow goes to the user as a <see cref="EventTypes.PermissionAsked"/> event. A harness that can't ask ignores it
    /// and allows everything, as before.
    /// </summary>
    Task ApplyPermissionsAsync(PermissionPolicy policy, CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Answers the pending ask <paramref name="requestId"/> with <paramref name="reply"/> (<see cref="PermissionReplies"/>);
    /// <paramref name="message"/> goes to the agent with a refusal. Throws <see cref="KeyNotFoundException"/> when no
    /// such ask waits: it was answered, or its turn ended.
    /// </summary>
    Task ReplyToPermissionAsync(string requestId, string reply, string? message, CancellationToken ct)
        => throw new KeyNotFoundException($"{HarnessType} sessions don't ask for permission.");

    /// <summary>Retrieve the message history for this instance.</summary>
    Task<MessagePage> GetMessagesAsync(MessageQuery? query, CancellationToken ct);

    /// <summary>Subscribe to a real-time stream of harness events.</summary>
    IAsyncEnumerable<HarnessEvent> SubscribeAsync(CancellationToken ct);

    /// <summary>
    /// Wait until the event subscription stream is established and ready to receive events.
    /// This ensures that events emitted immediately after activation/resume are not lost.
    /// Completes when the underlying transport (SSE, WebSocket, etc.) is connected and consuming.
    /// </summary>
    Task WaitForEventSubscriptionAsync(CancellationToken ct);

    /// <summary>Check whether this instance is still healthy.</summary>
    Task<HealthCheckResult> CheckHealthAsync(CancellationToken ct);

    /// <summary>
    /// Query the current activity status of the harness session (busy/idle).
    /// Returns null if the harness does not support activity status queries.
    /// Used for resync on SSE reconnect to correct missed state transitions.
    /// </summary>
    Task<string?> GetActivityStatusAsync(CancellationToken ct);

    /// <summary>
    /// Returns the agent's current todo list, or <see langword="null"/> when the harness can't report one
    /// or the query fails. Used to rebuild progress without waiting for the next todo update.
    /// </summary>
    Task<IReadOnlyList<Events.TodoEntry>?> GetTodosAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<Events.TodoEntry>?>(null);

    /// <summary>
    /// Stops one piece of running work the session reported (<see cref="WorkReport.WorkId"/>), leaving the session and
    /// its other work running. Returns false when the harness doesn't know that work (it ended already). The harness
    /// reports the end as a <see cref="EventTypes.WorkEnded"/> event. Throws <see cref="NotSupportedException"/> when
    /// the harness can't stop work on its own; such work is reported without <see cref="WorkReport.CanStop"/>.
    /// </summary>
    Task<bool> StopWorkAsync(string workId, CancellationToken ct)
        => throw new NotSupportedException($"{HarnessType} sessions can't stop running work on its own.");

    /// <summary>
    /// Reads running work's output from byte <paramref name="offset"/>: a page, and the offset to read from next. Null
    /// when the harness doesn't know that work. Throws <see cref="NotSupportedException"/> when the harness can't read
    /// output; such work is reported without <see cref="WorkReport.CanReadOutput"/>.
    /// </summary>
    Task<WorkOutput?> ReadWorkOutputAsync(string workId, long offset, CancellationToken ct)
        => throw new NotSupportedException($"{HarnessType} sessions can't read running work's output.");

    /// <summary>
    /// The work the harness itself says is running for this session now, so Fleet can catch up after it or the harness
    /// restarted: work Fleet still has running that isn't here ended with it (<see cref="WorkEndedReasons.Lost"/>).
    /// Null when the harness can't say.
    /// </summary>
    Task<IReadOnlyList<WorkReport>?> GetRunningWorkAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<WorkReport>?>(null);

    /// <summary>List available agents for this instance.</summary>
    Task<IReadOnlyList<AgentInfo>> GetAgentsAsync(CancellationToken ct);

    /// <summary>List available slash commands for this instance.</summary>
    Task<IReadOnlyList<CommandInfo>> GetCommandsAsync(CancellationToken ct);

    /// <summary>List available model providers for this instance.</summary>
    Task<IReadOnlyList<ProviderInfo>> GetProvidersAsync(CancellationToken ct);
}

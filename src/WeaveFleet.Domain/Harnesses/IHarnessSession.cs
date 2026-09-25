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
    /// Copies the conversation up to its last finished turn into a new harness session, for a side conversation
    /// (<c>/btw</c>). A turn still running isn't copied: the fork would carry on with it instead of answering. The
    /// session itself is left as it is. Null when the harness can't. Only for a harness with
    /// <see cref="HarnessCapabilities.SupportsSideConversations"/>.
    /// </summary>
    Task<SideConversationFork?> ForkSideConversationAsync(CancellationToken ct)
        => Task.FromResult<SideConversationFork?>(null);

    /// <summary>Abort the current agent operation.</summary>
    Task AbortAsync(CancellationToken ct);

    /// <summary>Answer a pending question request from the agent.</summary>
    Task AnswerQuestionAsync(string requestId, IReadOnlyList<IReadOnlyList<string>> answers, CancellationToken ct);

    /// <summary>Reject (dismiss) a pending question request from the agent.</summary>
    Task RejectQuestionAsync(string requestId, CancellationToken ct);

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

    /// <summary>List available agents for this instance.</summary>
    Task<IReadOnlyList<AgentInfo>> GetAgentsAsync(CancellationToken ct);

    /// <summary>List available slash commands for this instance.</summary>
    Task<IReadOnlyList<CommandInfo>> GetCommandsAsync(CancellationToken ct);

    /// <summary>List available model providers for this instance.</summary>
    Task<IReadOnlyList<ProviderInfo>> GetProvidersAsync(CancellationToken ct);
}

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Testing.Fakes;

public sealed class FakeHarnessSession : IHarnessSession
{
    private readonly Channel<HarnessEvent> _channel = Channel.CreateUnbounded<HarnessEvent>();

    public FakeHarnessSession(string instanceId)
    {
        InstanceId = instanceId;
    }

    // ── Configurable properties ──────────────────────────────────────────────

    public string InstanceId { get; }
    public int? ProcessId { get; set; }
    public string HarnessType { get; set; } = "opencode";
    public string? ResumeToken { get; set; }
    public HarnessSessionStatus Status { get; set; } = HarnessSessionStatus.Running;

    // ── Call-tracking for assertions ─────────────────────────────────────────

    // ConcurrentBag allows concurrent adds from multiple prompt tasks in concurrent activation tests.
    private readonly ConcurrentBag<(string Text, PromptOptions? Options)> _sendPromptCalls = [];
    private readonly ConcurrentBag<CommandOptions> _sendCommandCalls = [];

    /// <summary>Records each <see cref="SendPromptAsync"/> call for test assertions.</summary>
    public IReadOnlyList<(string Text, PromptOptions? Options)> SendPromptCalls => [.. _sendPromptCalls];

    /// <summary>Records each <see cref="SendCommandAsync"/> call for test assertions.</summary>
    public IReadOnlyList<CommandOptions> SendCommandCalls => [.. _sendCommandCalls];

    public bool StopCalled { get; private set; }
    public bool DeleteCalled { get; private set; }
    public bool ArchiveCalled { get; private set; }
    public bool AbortCalled { get; private set; }

    // ── Configurable behaviors ───────────────────────────────────────────────

    public Func<MessageQuery?, CancellationToken, Task<MessagePage>>? GetMessagesBehavior { get; set; }

    /// <summary>
    /// Optional override for <see cref="IHarnessSession.SendPromptAsync"/>, called after the call is
    /// recorded. Allows tests to make prompt delivery fail.
    /// </summary>
    public Func<string, PromptOptions?, CancellationToken, Task>? SendPromptBehavior { get; set; }

    /// <summary>
    /// Optional override for <see cref="IHarnessSession.DeleteAsync"/>. Allows tests to assert that
    /// best-effort delete is called during rollback scenarios.
    /// </summary>
    public Func<CancellationToken, Task>? DeleteBehavior { get; set; }

    /// <summary>
    /// Optional override for <see cref="IHarnessSession.GetActivityStatusAsync"/>. Allows tests to
    /// control the activity status returned by the harness (for resync testing).
    /// </summary>
    public Func<CancellationToken, Task<string?>>? GetActivityStatusBehavior { get; set; }

    /// <summary>
    /// What <see cref="IHarnessSession.GetTodosAsync"/> returns. <c>null</c> (the default) means the harness
    /// can't report a todo list.
    /// </summary>
    public IReadOnlyList<WeaveFleet.Domain.Events.TodoEntry>? Todos { get; set; }

    // ── Event emission (for streaming tests) ─────────────────────────────────

    public void Emit(HarnessEvent evt) => _channel.Writer.TryWrite(evt);
    public void Complete() => _channel.Writer.Complete();

    // ── IHarnessSession ──────────────────────────────────────────────────────

    public Task SendPromptAsync(string text, PromptOptions? options, CancellationToken ct)
    {
        _sendPromptCalls.Add((text, options));
        return SendPromptBehavior?.Invoke(text, options, ct) ?? Task.CompletedTask;
    }

    /// <summary>
    /// The id <see cref="SendCommandAsync"/> reports for the command's user message: by default the one it was given,
    /// as a harness that stores the message under Fleet's id does.
    /// </summary>
    public Func<CommandOptions, string?> CommandMessageIdBehavior { get; set; } = options => options.MessageId;

    public Task<string?> SendCommandAsync(CommandOptions options, CancellationToken ct)
    {
        _sendCommandCalls.Add(options);
        return Task.FromResult(CommandMessageIdBehavior(options));
    }

    private readonly ConcurrentBag<ShellCommandOptions> _shellCommandCalls = [];

    /// <summary>Records each <see cref="RunShellCommandAsync"/> call for test assertions.</summary>
    public IReadOnlyList<ShellCommandOptions> ShellCommandCalls => [.. _shellCommandCalls];

    /// <summary>Optional override for <see cref="RunShellCommandAsync"/>, called after the call is recorded.</summary>
    public Func<ShellCommandOptions, CancellationToken, Task>? RunShellCommandBehavior { get; set; }

    public Task RunShellCommandAsync(ShellCommandOptions options, CancellationToken ct)
    {
        _shellCommandCalls.Add(options);
        return RunShellCommandBehavior?.Invoke(options, ct) ?? Task.CompletedTask;
    }

    private readonly ConcurrentBag<CompactOptions> _compactCalls = [];

    /// <summary>Records each <see cref="CompactAsync"/> call for test assertions.</summary>
    public IReadOnlyList<CompactOptions> CompactCalls => [.. _compactCalls];

    /// <summary>Optional override for <see cref="CompactAsync"/>, called after the call is recorded.</summary>
    public Func<CompactOptions, CancellationToken, Task>? CompactBehavior { get; set; }

    public Task CompactAsync(CompactOptions options, CancellationToken ct)
    {
        _compactCalls.Add(options);
        return CompactBehavior?.Invoke(options, ct) ?? Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        StopCalled = true;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(CancellationToken ct)
    {
        DeleteCalled = true;
        return DeleteBehavior?.Invoke(ct) ?? Task.CompletedTask;
    }

    public Task ArchiveAsync(CancellationToken ct)
    {
        ArchiveCalled = true;
        return Task.CompletedTask;
    }

    public Task AbortAsync(CancellationToken ct)
    {
        AbortCalled = true;
        return Task.CompletedTask;
    }

    /// <summary>When true, answering or rejecting a question throws <see cref="NotSupportedException"/>.</summary>
    public bool QuestionsNotSupported { get; set; }

    /// <summary>The questions answered, in order, with their answers.</summary>
    public List<(string RequestId, IReadOnlyList<IReadOnlyList<string>> Answers)> AnsweredQuestions { get; } = [];

    /// <summary>The questions rejected, in order.</summary>
    public List<string> RejectedQuestions { get; } = [];

    public Task AnswerQuestionAsync(string requestId, IReadOnlyList<IReadOnlyList<string>> answers, CancellationToken ct)
    {
        if (QuestionsNotSupported)
            throw new NotSupportedException("This harness can't answer questions.");
        AnsweredQuestions.Add((requestId, answers));
        return Task.CompletedTask;
    }

    public Task RejectQuestionAsync(string requestId, CancellationToken ct)
    {
        if (QuestionsNotSupported)
            throw new NotSupportedException("This harness can't reject questions.");
        RejectedQuestions.Add(requestId);
        return Task.CompletedTask;
    }

    /// <summary>The asks <see cref="ReplyToPermissionAsync"/> knows; any other request id is <see cref="KeyNotFoundException"/>.</summary>
    public HashSet<string> PendingPermissions { get; } = [];

    /// <summary>The permission replies given, in order.</summary>
    public List<(string RequestId, string Reply, string? Message)> PermissionReplies { get; } = [];

    public Task ReplyToPermissionAsync(string requestId, string reply, string? message, CancellationToken ct)
    {
        if (!PendingPermissions.Remove(requestId))
            throw new KeyNotFoundException(requestId);
        PermissionReplies.Add((requestId, reply, message));
        return Task.CompletedTask;
    }

    public Task<MessagePage> GetMessagesAsync(MessageQuery? query, CancellationToken ct)
        => GetMessagesBehavior?.Invoke(query, ct)
           ?? Task.FromResult(new MessagePage([], false));

    /// <summary>How many subscriptions throw before one streams, to test that a failed pump recovers.</summary>
    public int FailingSubscriptions { get; set; }

    /// <summary>How many times <see cref="SubscribeAsync"/> has been started.</summary>
    public int SubscriptionCount => Volatile.Read(ref _subscriptionCount);

    private int _subscriptionCount;

    public async IAsyncEnumerable<HarnessEvent> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (Interlocked.Increment(ref _subscriptionCount) <= FailingSubscriptions)
            throw new InvalidOperationException("Scripted subscription failure.");

        await foreach (var evt in _channel.Reader.ReadAllAsync(ct))
            yield return evt;
    }

    public Task<HealthCheckResult> CheckHealthAsync(CancellationToken ct)
        => Task.FromResult(new HealthCheckResult(true, null));

    /// <summary>Answer returned by <see cref="AskOffTheRecordAsync"/>; null means "can't answer".</summary>
    public string? OffTheRecordAnswer { get; set; }

    /// <summary>Prompts passed to <see cref="AskOffTheRecordAsync"/>, in order.</summary>
    public List<string> OffTheRecordPrompts { get; } = [];

    public Task<string?> AskOffTheRecordAsync(string prompt, CancellationToken ct)
    {
        OffTheRecordPrompts.Add(prompt);
        return Task.FromResult(OffTheRecordAnswer);
    }

    /// <summary>What <see cref="StartOffTheRecordAsync"/> returns; null (the default) means "can't".</summary>
    public IOffTheRecordConversation? OffTheRecordConversation { get; set; }

    public Task<IOffTheRecordConversation?> StartOffTheRecordAsync(CancellationToken ct)
        => Task.FromResult(OffTheRecordConversation);

    /// <summary>The policies <see cref="ApplyPermissionsAsync"/> was given, in order.</summary>
    public List<PermissionPolicy> AppliedPermissions { get; } = [];

    public Task ApplyPermissionsAsync(PermissionPolicy policy, CancellationToken ct)
    {
        AppliedPermissions.Add(policy);
        return Task.CompletedTask;
    }

    /// <summary>What <see cref="ForkConversationAsync"/> returns; null (the default) means "can't".</summary>
    public ConversationFork? ConversationFork { get; set; }

    /// <summary>How many times <see cref="ForkConversationAsync"/> was called.</summary>
    public int ConversationForks { get; private set; }

    public Task<ConversationFork?> ForkConversationAsync(CancellationToken ct)
    {
        ConversationForks++;
        return Task.FromResult(ConversationFork);
    }

    /// <summary>What <see cref="StopWorkAsync"/> answers for a work id; unset, the harness can't stop work.</summary>
    public Func<string, bool>? StopWorkBehavior { get; set; }

    /// <summary>The work ids <see cref="StopWorkAsync"/> was asked to stop.</summary>
    public List<string> StopWorkCalls { get; } = [];

    public Task<bool> StopWorkAsync(string workId, CancellationToken ct)
    {
        if (StopWorkBehavior is null)
            throw new NotSupportedException("This harness can't stop running work on its own.");
        StopWorkCalls.Add(workId);
        return Task.FromResult(StopWorkBehavior(workId));
    }

    /// <summary>What <see cref="ReadWorkOutputAsync"/> answers for a work id and offset; unset, the harness can't read output.</summary>
    public Func<string, long, WorkOutput?>? WorkOutputBehavior { get; set; }

    public Task<WorkOutput?> ReadWorkOutputAsync(string workId, long offset, CancellationToken ct)
        => WorkOutputBehavior is { } read
            ? Task.FromResult(read(workId, offset))
            : throw new NotSupportedException("This harness can't read running work's output.");

    /// <summary>What <see cref="GetRunningWorkAsync"/> answers; null (the default) means the harness can't say.</summary>
    public IReadOnlyList<WorkReport>? RunningWork { get; set; }

    public Task<IReadOnlyList<WorkReport>?> GetRunningWorkAsync(CancellationToken ct) => Task.FromResult(RunningWork);

    public Task<string?> GetActivityStatusAsync(CancellationToken ct)
        => GetActivityStatusBehavior?.Invoke(ct) ?? Task.FromResult<string?>("idle");

    public Task<IReadOnlyList<WeaveFleet.Domain.Events.TodoEntry>?> GetTodosAsync(CancellationToken ct)
        => Task.FromResult(Todos);

    public Task WaitForEventSubscriptionAsync(CancellationToken ct)
    {
        // Fake harness session uses an unbounded channel which is immediately ready.
        // Tests that need to verify subscription timing should use SubscriptionGatedHarnessSession.
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <summary>What <see cref="GetAgentsAsync"/> lists.</summary>
    public IReadOnlyList<AgentInfo> Agents { get; set; } = [];

    /// <summary>What <see cref="GetCommandsAsync"/> lists.</summary>
    public IReadOnlyList<CommandInfo> Commands { get; set; } = [];

    /// <summary>What <see cref="GetProvidersAsync"/> lists.</summary>
    public IReadOnlyList<ProviderInfo> Providers { get; set; } = [];

    public Task<IReadOnlyList<AgentInfo>> GetAgentsAsync(CancellationToken ct)
        => Task.FromResult(Agents);

    public Task<IReadOnlyList<CommandInfo>> GetCommandsAsync(CancellationToken ct)
        => Task.FromResult(Commands);

    public Task<IReadOnlyList<ProviderInfo>> GetProvidersAsync(CancellationToken ct)
        => Task.FromResult(Providers);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

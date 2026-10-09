using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Analytics;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode2;

/// <summary>What an <see cref="OpenCode2HarnessSession"/> knows about the Fleet session it serves.</summary>
internal sealed record OpenCode2SessionContext(
    string FleetSessionId,
    string OwnerUserId,
    string WorkingDirectory,
    string? ProjectId,
    string? ProjectName);

/// <summary>
/// One Fleet session on an OpenCode 2 server. The session lives in the server's database under
/// <see cref="ResumeToken"/>; this object only attaches to the server's event stream and sends requests. When the
/// server stops (an idle profile server does), the next request attaches the session to a new server for the owner and
/// the same profile.
/// </summary>
internal sealed partial class OpenCode2HarnessSession : IHarnessSession, IOpenCode2EventSink
{
    public const string Type = "opencode2";

    /// <summary>How many of the newest messages a catch-up reads: enough for the turn that ran while the stream was down.</summary>
    private const int ResyncMessages = 20;

    /// <summary>How many messages a page holds when looking for a background notice newer than the page being read.</summary>
    private const int NoticeSearchPage = 100;

    /// <summary>
    /// How long a command waits, after V2 has run it, to hear which user message it became. V2 takes the message in
    /// while it runs the command, so the wait is only for its event to arrive; a command that runs as a subagent puts
    /// no message in this session, and waits it out.
    /// </summary>
    internal TimeSpan CommandMessageWait { get; set; } = TimeSpan.FromSeconds(5);

    private readonly OpenCode2SessionContext _context;
    private readonly Func<CancellationToken, Task<OpenCode2Server>> _servers;
    private readonly OpenCode2Mapper _mapper;
    private readonly IAnalyticsCollector? _analytics;
    private readonly ILogger _logger;

    // The work the agent left running, by its handle (a shell's id, a subagent's call), as last reported: what Stop,
    // Output and the running list go by. Written on the event pump, read on requests.
    private readonly ConcurrentDictionary<string, WorkReport> _work = new(StringComparer.Ordinal);

    // The work Fleet asked V2 to stop: V2's notice then says the removed shell failed, but the user stopped it.
    private readonly ConcurrentDictionary<string, byte> _stopping = new(StringComparer.Ordinal);
    // The models' limits from V2's model list, by provider/model; null while being read or when it has none.
    private readonly ConcurrentDictionary<string, OpenCode2ModelLimit?> _modelLimits = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _attachLock = new(1, 1);
    private readonly Channel<HarnessEvent> _events = Channel.CreateBounded<HarnessEvent>(new BoundedChannelOptions(1000)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = false,
        SingleWriter = false,
    });

    // The questions waiting on the user, by the tool call that asked: the client answers by call id.
    private readonly ConcurrentDictionary<string, OpenCode2Form> _questions = new(StringComparer.Ordinal);

    // The session's permission level, the asks waiting on the user, and what they said not to ask again about; and the
    // level V2's session rules were last set for.
    private readonly PermissionGate _permissions = new();
    private string? _rulesLevel;

    // The prompts sent that V2 hasn't taken into the conversation yet, by the id Fleet gave them. A user message V2
    // takes in that isn't one of them is a command's: V2's command route neither takes an id nor returns one.
    private readonly ConcurrentDictionary<string, byte> _promptsNotTakenIn = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private volatile TaskCompletionSource<string>? _commandMessage;

    private OpenCode2Server? _server;
    private volatile HarnessSessionStatus _status = HarnessSessionStatus.Idle;

    // The agent and model the V2 session runs with now, so a prompt switches only what it changes.
    private volatile string? _agent;
    private volatile OpenCode2ModelRef? _model;
    private bool _disposed;

    /// <param name="info">The V2 session as the server last described it (its agent and model).</param>
    /// <param name="servers">The running server for the owner and the session's profile, started when there's none.</param>
    internal OpenCode2HarnessSession(
        string instanceId,
        OpenCode2SessionInfo info,
        OpenCode2SessionContext context,
        OpenCode2Server server,
        Func<CancellationToken, Task<OpenCode2Server>> servers,
        IAnalyticsCollector? analytics,
        ILogger logger)
    {
        InstanceId = instanceId;
        ResumeToken = info.Id ?? throw new ArgumentException("The V2 session has no id.", nameof(info));
        _context = context;
        _servers = servers;
        _analytics = analytics;
        _logger = logger;
        _agent = info.Agent;
        _model = info.Model;
        _mapper = new OpenCode2Mapper(context.FleetSessionId, context.WorkingDirectory);
        Attach(server);
    }

    public string InstanceId { get; }

    public OpenCode2SessionContext Context => _context;

    public int? ProcessId => _server?.ProcessId;

    /// <summary>The V2 session id (<c>ses_…</c>).</summary>
    public string ResumeToken { get; }

    public string HarnessType => Type;

    public HarnessSessionStatus Status => _status;

    public async Task SendPromptAsync(string text, PromptOptions? options, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var server = await AttachedServerAsync(ct).ConfigureAwait(false);
        LogPrompt(_logger, InstanceId);

        // V2 keeps the agent and model on the session, not the prompt.
        await ApplyChoicesAsync(server, options?.Agent, options?.ProviderId, options?.ModelId, options?.Effort, ct).ConfigureAwait(false);

        // The user message keeps the id Fleet showed it with (V2 takes ids of the same msg_ form).
        // The status follows V2's execution events: a short turn can be over before this request returns.
        if (options?.MessageId is { } messageId)
            _promptsNotTakenIn[messageId] = 0;

        // Fleet's notes to the model go in as synthetic messages that wait for the prompt, as V2's own notes do: the
        // model reads them, the conversation doesn't show them (OpenCode2History).
        foreach (var note in options?.ModelNotes ?? [])
            await server.Client.AddSyntheticAsync(ResumeToken, note, ct).ConfigureAwait(false);
        await server.Client.PromptAsync(ResumeToken, text, options?.MessageId, PromptFiles(options?.Attachments), Delivery(options?.Delivery), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// V2's delivery for a prompt. Fleet holds a queued prompt until the turn ends, so V2 keeps no queue of its own the
    /// user can't see; saying <c>queue</c> keeps a prompt that meets a turn Fleet didn't know was running from steering
    /// it. A prompt that doesn't say (Fleet's own, from automations, workflows and other sessions) gets V2's default,
    /// which steers.
    /// </summary>
    internal static string? Delivery(PromptDelivery? delivery) => delivery switch
    {
        PromptDelivery.Steer => OpenCode2Deliveries.Steer,
        PromptDelivery.Queue => OpenCode2Deliveries.Queue,
        _ => null,
    };

    /// <summary>A prompt's attachments (pasted images) as V2's prompt files: inline, as <c>data:</c> URIs.</summary>
    internal static IReadOnlyList<OpenCode2PromptFile>? PromptFiles(IReadOnlyList<HarnessAttachment>? attachments)
        => attachments is { Count: > 0 }
            ? attachments.Select(a => new OpenCode2PromptFile { Uri = $"data:{a.Mime};base64,{a.Data}", Name = a.Filename }).ToList()
            : null;

    public async Task AbortAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        LogAbort(_logger, InstanceId);

        // V2 answers with session.execution.interrupted, which ends the turn.
        var server = await AttachedServerAsync(ct).ConfigureAwait(false);
        await server.Client.InterruptAsync(ResumeToken, ct).ConfigureAwait(false);
    }

    public Task WaitForEventSubscriptionAsync(CancellationToken ct)
        => WaitForEventsCoreAsync(ct);

    public async Task<string?> GetActivityStatusAsync(CancellationToken ct)
    {
        if (_server is not { IsRunning: true } server)
            return null;

        try
        {
            var active = await server.Client.GetActiveSessionIdsAsync(ct).ConfigureAwait(false);
            if (!active.Contains(ResumeToken))
                return ActivityStatuses.Idle;

            // A turn stopped on a question needs the user. Read the forms too: after a restart Fleet hasn't seen them.
            foreach (var form in await server.Client.GetFormsAsync(ResumeToken, ct).ConfigureAwait(false))
                RememberQuestion(form);
            return _questions.IsEmpty ? ActivityStatuses.Busy : ActivityStatuses.WaitingInput;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    public Task<HealthCheckResult> CheckHealthAsync(CancellationToken ct)
        => Task.FromResult(_server is { IsRunning: true }
            ? new HealthCheckResult(true, null)
            : new HealthCheckResult(false, "The OpenCode 2 server isn't running; the next prompt starts it again."));

    /// <summary>Stops listening for this session. The server keeps running for the owner's other sessions.</summary>
    public async Task StopAsync(CancellationToken ct)
    {
        if (_status is HarnessSessionStatus.Stopped)
            return;

        _status = HarnessSessionStatus.Stopping;
        var server = _server;
        if (server is { IsRunning: true })
        {
            // A stopped session doesn't go on working where nobody can see it.
            try
            {
                var active = await server.Client.GetActiveSessionIdsAsync(ct).ConfigureAwait(false);
                if (active.Contains(ResumeToken))
                    await server.Client.InterruptAsync(ResumeToken, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                LogStopInterruptFailed(_logger, InstanceId, ex);
            }

            server.Detach(ResumeToken, this);
        }

        _status = HarnessSessionStatus.Stopped;
        _events.Writer.TryComplete();
    }

    /// <summary>Deletes the V2 session from the server's database, then stops.</summary>
    public async Task DeleteAsync(CancellationToken ct)
    {
        if (_server is { IsRunning: true } server)
        {
            try
            {
                await server.Client.DeleteSessionAsync(ResumeToken, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                LogDeleteFailed(_logger, InstanceId, ex);
            }
        }

        await StopAsync(ct).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<HarnessEvent> SubscribeAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var evt in _events.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return evt;
    }

    /// <summary>
    /// The session's history from V2, oldest first. <see cref="MessageQuery.Before"/> is the cursor a previous page
    /// returned, for the page of older messages.
    /// </summary>
    public async Task<MessagePage> GetMessagesAsync(MessageQuery? query, CancellationToken ct)
    {
        var server = await AttachedServerAsync(ct).ConfigureAwait(false);
        var page = await server.Client.GetMessagesAsync(ResumeToken, query?.Limit, query?.Before, ct).ConfigureAwait(false);
        var messages = page.Data ?? [];

        // V2 sends a next cursor with every page, the last one too; a page that isn't full is the last.
        var hasMore = page.Cursor?.Next is not null
            && messages.Count > 0
            && (query?.Limit is not { } limit || messages.Count >= limit);
        var history = OpenCode2History.ToHarnessMessages(messages.Reverse());
        history = await SettleLostBackgroundWorkAsync(server, history, messages, ct).ConfigureAwait(false);
        if (query?.Before is null)
            history = [.. history, .. await PendingPromptsAsync(server, history, ct).ConfigureAwait(false)];

        return new MessagePage(history, hasMore, hasMore ? page.Cursor!.Next : null);
    }

    /// <summary>
    /// <paramref name="history"/> with each call whose background work is gone shown failed
    /// (<see cref="OpenCode2Mapper.BackgroundWorkLost"/>). V2 stores a backgrounded call as running for good and says
    /// the work finished only with a notice, but it keeps the work in the server process: when that stopped (Fleet
    /// restarted, or it crashed), the work stopped with it and no notice ever comes. So work the server isn't running
    /// now (no such shell, no such child session working) that has no notice is gone.
    /// </summary>
    /// <param name="messages">The page as V2 sent it, newest first, which holds the notices.</param>
    private async Task<IReadOnlyList<HarnessMessage>> SettleLostBackgroundWorkAsync(
        OpenCode2Server server,
        IReadOnlyList<HarnessMessage> history,
        IReadOnlyList<OpenCode2Message> messages,
        CancellationToken ct)
    {
        var waiting = OpenCode2History.BackgroundHandles(history);
        waiting.ExceptWith(OpenCode2History.NoticeHandles(messages));
        if (waiting.Count == 0)
            return history;

        try
        {
            waiting.ExceptWith(await server.Client.GetActiveSessionIdsAsync(ct).ConfigureAwait(false));
            // Only a loaded folder can have a shell running, and asking about one that isn't would load it.
            foreach (var directory in waiting.Count > 0 ? await server.Client.GetLoadedLocationsAsync(ct).ConfigureAwait(false) : [])
            {
                var shells = await server.Client.GetRunningShellsAsync(directory, ct).ConfigureAwait(false);
                waiting.ExceptWith(shells.Where(shell => shell.Status == "running").Select(shell => shell.Id).OfType<string>());
            }
            if (waiting.Count == 0)
                return history;

            // Read after what's running, so work that ended since the page was read has its notice by now: in the
            // inbox while V2 hasn't delivered it, or in a message newer than the page.
            waiting.ExceptWith(OpenCode2History.NoticeHandles(await server.Client.GetInboxAsync(ResumeToken, ct).ConfigureAwait(false)));
            if (waiting.Count > 0)
                waiting.ExceptWith(await NoticesDownToAsync(server, messages, ct).ConfigureAwait(false));

            return waiting.Count == 0 ? history : OpenCode2History.SettleLostBackgroundWork(history, waiting);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            // Unsettled, the call only goes on showing Background.
            LogBackgroundCheckFailed(_logger, InstanceId, ex);
            return history;
        }
    }

    /// <summary>The handles of the notices from the newest message down to <paramref name="page"/>.</summary>
    private async Task<IReadOnlySet<string>> NoticesDownToAsync(OpenCode2Server server, IReadOnlyList<OpenCode2Message> page, CancellationToken ct)
    {
        var onPage = page.Select(message => message.Id).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var notices = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var newer = await server.Client.GetMessagesAsync(ResumeToken, NoticeSearchPage, cursor, ct).ConfigureAwait(false);
            var data = newer.Data ?? [];
            notices.UnionWith(OpenCode2History.NoticeHandles(data));
            if (data.Count < NoticeSearchPage || data.Any(message => message.Id is { } id && onPage.Contains(id)))
                break;
            cursor = newer.Cursor?.Next;
        }
        while (cursor is not null);

        return notices;
    }

    /// <summary>
    /// The prompts sent that V2 hasn't taken in yet (a steer waits for the turn's next step), newest last, so a reload
    /// mid-turn still shows them. Without them the history is still right, only short of those, so a failure is logged.
    /// </summary>
    private async Task<IReadOnlyList<HarnessMessage>> PendingPromptsAsync(
        OpenCode2Server server,
        IReadOnlyList<HarnessMessage> history,
        CancellationToken ct)
    {
        try
        {
            var shown = history.Select(message => message.Id).ToHashSet(StringComparer.Ordinal);
            var inbox = await server.Client.GetInboxAsync(ResumeToken, ct).ConfigureAwait(false);
            return OpenCode2History.PendingPrompts(inbox).Where(message => !shown.Contains(message.Id)).ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            LogInboxReadFailed(_logger, InstanceId, ex);
            return [];
        }
    }

    /// <summary>
    /// A child session holding this one's history up to its last finished turn: V2 copies what comes before the message
    /// it's given. A turn still running is left out, or the fork would carry on with it.
    /// </summary>
    public async Task<ConversationFork?> ForkConversationAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var server = await AttachedServerAsync(ct).ConfigureAwait(false);
        var before = await FindForkPointAsync(server, ct).ConfigureAwait(false);
        var fork = await server.Client.ForkSessionAsync(ResumeToken, before, ct).ConfigureAwait(false);
        try
        {
            // The newest message the fork holds marks where a side conversation starts.
            var newest = await server.Client.GetMessagesAsync(fork.Id!, 1, null, ct).ConfigureAwait(false);
            return new ConversationFork(fork.Id!, newest.Data is { Count: > 0 } data ? data[0].Id : null);
        }
        catch
        {
            await DeleteQuietlyAsync(server, fork.Id!).ConfigureAwait(false);
            throw;
        }
    }

    private const int ForkPageSize = 50;
    private const int ForkMaxPages = 10;

    /// <summary>
    /// The message a fork stops before: the first one after the last finished turn, or null when
    /// the session ends with one (copy it all). With no finished turn at all, the oldest message, so nothing is copied.
    /// </summary>
    private async Task<string?> FindForkPointAsync(OpenCode2Server server, CancellationToken ct)
    {
        string? cursor = null;
        string? firstAfterFinishedTurn = null;
        for (var page = 0; page < ForkMaxPages; page++)
        {
            var messages = await server.Client.GetMessagesAsync(ResumeToken, ForkPageSize, cursor, ct).ConfigureAwait(false);
            var data = messages.Data ?? [];
            foreach (var message in data)
            {
                if (EndsTurn(message))
                    return firstAfterFinishedTurn;
                firstAfterFinishedTurn = message.Id;
            }

            // V2 sends a next cursor with every page; one that isn't full is the last.
            if (data.Count < ForkPageSize || messages.Cursor?.Next is not { } next)
                return firstAfterFinishedTurn;
            cursor = next;
        }

        return firstAfterFinishedTurn;
    }

    /// <summary>
    /// Whether a message is the last of a finished turn. A V2 turn is several assistant steps, each completed on its
    /// own; the turn goes on after a step that stopped for tool calls, and ends with any other, or an error.
    /// </summary>
    internal static bool EndsTurn(OpenCode2Message message)
        => message is { Type: "assistant", Time.Completed: not null }
            && (message.Finish != "tool-calls" || message.Error is not null);

    private async Task DeleteQuietlyAsync(OpenCode2Server server, string sessionId)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await server.Client.DeleteSessionAsync(sessionId, cleanup.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            LogDeleteFailed(_logger, sessionId, ex);
        }
    }

    /// <summary>V2's <c>generate</c>: an answer from the session's conversation, by its agent and model, left out of its history.</summary>
    public async Task<string?> AskOffTheRecordAsync(string prompt, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var server = await AttachedServerAsync(ct).ConfigureAwait(false);
        return await server.Client.GenerateAsync(ResumeToken, prompt, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// V2's <c>generate</c> again, once per question: each follow-up carries the questions and answers before it, since
    /// V2 keeps nothing between calls.
    /// </summary>
    public async Task<IOffTheRecordConversation?> StartOffTheRecordAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var server = await AttachedServerAsync(ct).ConfigureAwait(false);
        var sessionId = ResumeToken;
        return new OpenCode2OffTheRecordConversation(
            (prompt, token) => server.Client.GenerateAsync(sessionId, prompt, token),
            OpenCode2HarnessRuntime.ConversationQuestionTimeout);
    }

    /// <summary>
    /// Runs one of V2's commands as the next turn; V2 expands its template into the user's message, under an id of its
    /// own. That id is read off V2's inbox (<see cref="OnEvent"/>), one command at a time. A skill, which V2 has no
    /// command for, goes as a prompt that names it, under Fleet's id: V2 adds the skill's content for the model.
    /// </summary>
    public async Task<string?> SendCommandAsync(CommandOptions options, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var server = await AttachedServerAsync(ct).ConfigureAwait(false);
        LogCommand(_logger, InstanceId, options.Command);

        if (await OpenCode2Catalog.FindSkillAsync(server, _context.WorkingDirectory, options.Command, ct).ConfigureAwait(false) is { } skill)
        {
            await ApplyChoicesAsync(server, options.Agent, options.ProviderId, options.ModelId, effort: null, ct).ConfigureAwait(false);
            if (options.MessageId is { } messageId)
                _promptsNotTakenIn[messageId] = 0;
            await server.Client.PromptAsync(
                ResumeToken, CommandFormatting.FormatCommandPrompt(options), options.MessageId, files: null, delivery: null, ct, [skill])
                .ConfigureAwait(false);
            return options.MessageId;
        }

        await ApplyChoicesAsync(server, options.Agent, options.ProviderId, options.ModelId, effort: null, ct).ConfigureAwait(false);

        await _commandLock.WaitAsync(ct).ConfigureAwait(false);
        var message = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _commandMessage = message;
        try
        {
            await server.Client.RunCommandAsync(ResumeToken, options.Command, options.Arguments ?? string.Empty, ct).ConfigureAwait(false);
            return await message.Task.WaitAsync(CommandMessageWait, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            LogCommandMessageNotSeen(_logger, InstanceId, options.Command);
            return null;
        }
        finally
        {
            _commandMessage = null;
            _commandLock.Release();
        }
    }

    /// <summary>
    /// Runs a command the user typed in the session's folder, under the message id Fleet gave it. V2 runs one during a
    /// turn too, and steers its output into the turn; Fleet's composer holds one back until the turn ends instead.
    /// The request can last as long as the command, so this waits only long enough to hear a refusal.
    /// </summary>
    public async Task RunShellCommandAsync(ShellCommandOptions options, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var server = await AttachedServerAsync(ct).ConfigureAwait(false);
        LogShellCommand(_logger, InstanceId);

        var sessionId = ResumeToken;
        await ShellCommandCall.RunUntilTakenAsync(
            token => server.Client.RunShellAsync(sessionId, options.MessageId, options.Command, token),
            ex => LogShellCommandFailed(_logger, InstanceId, ex),
            CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks V2 to compact the session's context. V2 answers once it has taken the request; the compaction's start
    /// and end arrive as events.
    /// </summary>
    public async Task CompactAsync(CompactOptions options, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var server = await AttachedServerAsync(ct).ConfigureAwait(false);
        await server.Client.CompactAsync(ResumeToken, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Answers the form a question tool call asked with. <paramref name="requestId"/> is the tool call's id (what
    /// the question card knows) or the form's; <paramref name="answers"/> has the chosen labels, one list per question.
    /// </summary>
    public async Task AnswerQuestionAsync(string requestId, IReadOnlyList<IReadOnlyList<string>> answers, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var server = await AttachedServerAsync(ct).ConfigureAwait(false);
        var form = await FindQuestionAsync(server, requestId, ct).ConfigureAwait(false);

        await server.Client.ReplyToFormAsync(ResumeToken, form.Id!, FormAnswer(form, answers), ct).ConfigureAwait(false);
        Forget(form.Id!);

        // The turn goes on; V2 doesn't say so itself, since it never stopped.
        _events.Writer.TryWrite(_mapper.Status(ActivityStatuses.Busy));
    }

    /// <summary>Dismisses the question. V2 then fails the tool call and interrupts the turn.</summary>
    public async Task RejectQuestionAsync(string requestId, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var server = await AttachedServerAsync(ct).ConfigureAwait(false);
        var form = await FindQuestionAsync(server, requestId, ct).ConfigureAwait(false);

        await server.Client.CancelFormAsync(ResumeToken, form.Id!, ct).ConfigureAwait(false);
        Forget(form.Id!);
    }

    public async Task<IReadOnlyList<AgentInfo>> GetAgentsAsync(CancellationToken ct)
        => await OpenCode2Catalog.ReadAgentsAsync(await AttachedServerAsync(ct).ConfigureAwait(false), _context.WorkingDirectory, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<CommandInfo>> GetCommandsAsync(CancellationToken ct)
        => await OpenCode2Catalog.ReadCommandsAsync(await AttachedServerAsync(ct).ConfigureAwait(false), _context.WorkingDirectory, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<ProviderInfo>> GetProvidersAsync(CancellationToken ct)
        => await OpenCode2Catalog.ReadProvidersAsync(await AttachedServerAsync(ct).ConfigureAwait(false), _context.WorkingDirectory, ct).ConfigureAwait(false);

    /// <inheritdoc />
    /// <remarks>Called on the server's event pump, one event at a time.</remarks>
    public void OnEvent(OpenCode2Event evt)
    {
        // Read before mapping: the mapper forgets a step's model once the step ends.
        if (_analytics is not null
            && _mapper.TryReadStepUsage(evt, _context.ProjectId, _context.ProjectName, _context.WorkingDirectory, _context.OwnerUserId) is { } usage)
        {
            _analytics.AcceptTokenEvent(usage);
        }

        // Read before mapping too: the mapper forgets a step's model once the step ends.
        if (_mapper.TryReadContextCall(evt) is { } contextCall)
            Write(ContextUsageEvent(contextCall.Call, contextCall.ProviderId, contextCall.ModelId));

        if (_mapper.TryMapCompaction(evt) is { } compaction)
            Write(compaction);

        // Read before mapping too: the mapper forgets a tool call once it ends. A subagent call is running work, which Fleet
        // records with its child session.
        if (_mapper.TryReadDelegation(evt) is { } delegation)
            Write(_mapper.WorkEvent(delegation, called: evt.Type == "session.tool.called"));

        if (OpenCode2Mapper.ReadUserMessageTakenIn(evt) is { } userMessageId && !_promptsNotTakenIn.TryRemove(userMessageId, out _))
            _commandMessage?.TrySetResult(userMessageId);

        switch (evt.Type)
        {
            case "session.execution.started":
                SetStatus(HarnessSessionStatus.Running);
                break;
            case "session.execution.succeeded" or "session.execution.failed" or "session.execution.interrupted":
                SetStatus(HarnessSessionStatus.Idle);
                // A turn that ended takes its asks with it.
                foreach (var entry in _permissions.Pending.Clear())
                    _events.Writer.TryWrite(PermissionEvents.Replied(entry.Ask, PermissionReplies.Gone, _context.FleetSessionId));
                break;
            case "form.created" when evt.Data.TryGetProperty("form", out var form) && form.ValueKind == JsonValueKind.Object:
                if (form.Deserialize(OpenCode2JsonContext.Default.OpenCode2Form) is { } created)
                    RememberQuestion(created);
                break;
            case "form.replied" or "form.cancelled" when evt.Data.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String:
                Forget(id.GetString()!);
                break;
            case "permission.asked":
                OnPermissionAsked(evt.Data, subagent: false);
                break;
            case "permission.replied" when PermissionEvents.String(evt.Data, "requestID") is { } repliedId:
                SettlePermission(repliedId, PermissionEvents.String(evt.Data, "reply") ?? PermissionReplies.Once);
                break;
            // Switched by Fleet, or by anyone else using the session: the next prompt compares with this.
            case "session.agent.selected" when evt.Data.TryGetProperty("agent", out var agent) && agent.ValueKind == JsonValueKind.String:
                _agent = agent.GetString();
                break;
            case "session.model.selected" when evt.Data.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.Object:
                _model = model.Deserialize(OpenCode2JsonContext.Default.OpenCode2ModelRef);
                break;
        }

        foreach (var harnessEvent in _mapper.Map(evt))
            Write(harnessEvent);
    }

    /// <summary>
    /// A step's size, with its model's window when V2's model list has been read. The first time a model's isn't
    /// known, the list is read in the background and the window follows in a report of its own.
    /// </summary>
    private HarnessEvent ContextUsageEvent(ContextCall call, string? providerId, string? modelId)
    {
        var key = ModelKey(providerId, modelId);
        var limit = key is not null && _modelLimits.TryGetValue(key, out var known) ? known : null;
        if (key is not null && !_modelLimits.ContainsKey(key))
            _ = ReadModelLimitsAsync(providerId!, modelId!, key);

        return ContextEvents.Usage(
            new ContextUsageReport
            {
                Call = call,
                Limit = limit is { Context: > 0 } ? limit.Context : null,
                ProviderId = providerId,
                ModelId = modelId,
            },
            _context.FleetSessionId,
            _context.FleetSessionId);
    }

    private async Task ReadModelLimitsAsync(string providerId, string modelId, string key)
    {
        if (!_modelLimits.TryAdd(key, null))
            return;

        try
        {
            var server = await AttachedServerAsync(CancellationToken.None).ConfigureAwait(false);
            var models = await server.Client.GetModelsAsync(_context.WorkingDirectory, CancellationToken.None).ConfigureAwait(false);
            foreach (var model in models)
            {
                if (ModelKey(model.ProviderId, model.Id) is { } modelKey && model.Limit is { Context: > 0 } limit)
                    _modelLimits[modelKey] = limit;
            }

            if (_modelLimits.TryGetValue(key, out var found) && found is not null)
            {
                Write(ContextEvents.Usage(
                    new ContextUsageReport { Limit = found.Context, ProviderId = providerId, ModelId = modelId },
                    _context.FleetSessionId,
                    _context.FleetSessionId));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Asked again with the model's next step.
            _modelLimits.TryRemove(key, out _);
            LogModelLimitsUnavailable(_logger, modelId, ex);
        }
    }

    private static string? ModelKey(string? providerId, string? modelId)
        => string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(modelId) ? null : $"{providerId}/{modelId}";

    /// <summary>Passes an event on, keeping <see cref="_work"/> up to date with the work events among them.</summary>
    private void Write(HarnessEvent harnessEvent)
    {
        if (EventTypes.IsWorkEvent(harnessEvent.Type) && WorkEvents.Read(harnessEvent) is { } report)
        {
            if (harnessEvent.Type == EventTypes.WorkEnded)
            {
                _work.TryRemove(report.WorkId, out _);
                if (_stopping.TryRemove(report.WorkId, out _) && report.EndedReason != WorkEndedReasons.Lost)
                    harnessEvent = WorkEvents.Ended(report, WorkEndedReasons.Cancelled, harnessEvent.SessionId, harnessEvent.FleetSessionId);
            }
            else
                _work.AddOrUpdate(report.WorkId, report, (_, known) => Merge(known, report));
        }

        _events.Writer.TryWrite(harnessEvent);
    }

    private static WorkReport Merge(WorkReport known, WorkReport update) => known with
    {
        Title = update.Title ?? known.Title,
        Label = update.Label ?? known.Label,
        ToolCallId = update.ToolCallId ?? known.ToolCallId,
        ChildHarnessSessionId = update.ChildHarnessSessionId ?? known.ChildHarnessSessionId,
        Background = update.Background ?? known.Background,
        CanStop = update.CanStop ?? known.CanStop,
        CanReadOutput = update.CanReadOutput ?? known.CanReadOutput,
    };

    /// <inheritdoc />
    /// <remarks>
    /// A shell is removed (<c>DELETE /api/shell/{id}</c>), which ends its process; a subagent's child session is
    /// interrupted. V2's notice that the work ended follows.
    /// </remarks>
    public async Task<bool> StopWorkAsync(string workId, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var server = await AttachedServerAsync(ct).ConfigureAwait(false);
        _work.TryGetValue(workId, out var work);

        // Marked first: V2's notice of the end can arrive before its answer to the request.
        _stopping[workId] = 0;
        var stopped = false;
        try
        {
            if (work?.Kind == WorkKinds.Shell || (work is null && workId.StartsWith(ShellIdPrefix, StringComparison.Ordinal)))
            {
                stopped = await server.Client.RemoveShellAsync(_context.WorkingDirectory, workId, ct).ConfigureAwait(false);
            }
            else if (work?.ChildHarnessSessionId is { } child)
            {
                await server.Client.InterruptAsync(child, ct).ConfigureAwait(false);
                stopped = true;
            }

            return stopped;
        }
        finally
        {
            if (!stopped)
                _stopping.TryRemove(workId, out _);
        }
    }

    /// <inheritdoc />
    /// <remarks>Only a shell has output of its own; a subagent's is its child session's conversation.</remarks>
    public async Task<WorkOutput?> ReadWorkOutputAsync(string workId, long offset, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_work.TryGetValue(workId, out var work) ? work.Kind != WorkKinds.Shell : !workId.StartsWith(ShellIdPrefix, StringComparison.Ordinal))
            throw new NotSupportedException("A subagent's output is its session's conversation: open its session.");

        var server = await AttachedServerAsync(ct).ConfigureAwait(false);
        var output = await server.Client.GetShellOutputAsync(_context.WorkingDirectory, workId, offset, ct).ConfigureAwait(false);
        return output is null ? null : new WorkOutput(output.Output ?? string.Empty, output.Cursor, output.Size, output.Truncated);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Asked of the server, which may have run the work while no Fleet session listened: the shells it runs for this
    /// session, and the child sessions of this one it has working. A server that stopped took all of it with it.
    /// </remarks>
    public async Task<IReadOnlyList<WorkReport>?> GetRunningWorkAsync(CancellationToken ct)
    {
        if (_server is not { IsRunning: true } server)
            return [];

        try
        {
            var running = new List<WorkReport>();
            var shells = await server.Client.GetRunningShellsAsync(_context.WorkingDirectory, ct).ConfigureAwait(false);
            running.AddRange(shells
                .Where(shell => shell is { Id: not null, Status: "running" }
                    && shell.Metadata.ValueKind == JsonValueKind.Object
                    && shell.Metadata.TryGetProperty("sessionID", out var owner)
                    && owner.ValueKind == JsonValueKind.String
                    && owner.GetString() == ResumeToken)
                .Select(shell => _work.GetValueOrDefault(shell.Id!) ?? new WorkReport
                {
                    WorkId = shell.Id!,
                    Kind = WorkKinds.Shell,
                    Title = OpenCode2Mapper.ShellTool,
                    Label = shell.Command,
                    Background = true,
                    CanStop = true,
                    CanReadOutput = true,
                }));

            // A child is known by its session: the call that started it may be one this session object never saw.
            var active = await server.Client.GetActiveSessionIdsAsync(ct).ConfigureAwait(false);
            foreach (var id in active.Where(id => id != ResumeToken))
            {
                if ((await server.Client.GetSessionAsync(id, ct).ConfigureAwait(false))?.ParentID != ResumeToken)
                    continue;
                running.Add(_work.Values.FirstOrDefault(w => w.ChildHarnessSessionId == id)
                    ?? new WorkReport { WorkId = id, Kind = WorkKinds.Subagent, ChildHarnessSessionId = id });
            }

            return running;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // Unsure what runs: better to leave Fleet's list as it is than end work that may still be going.
            LogBackgroundCheckFailed(_logger, InstanceId, ex);
            return null;
        }
    }

    /// <summary>The start of V2's shell ids (<c>sh_…</c>).</summary>
    private const string ShellIdPrefix = "sh_";

    /// <inheritdoc />
    /// <remarks>
    /// V2 doesn't replay what was sent while the stream was down, so this reads what changed: the messages of a
    /// turn that ran (their parts update in place), the questions waiting, and whether a turn is still running. A
    /// turn that ended in the gap ends here.
    /// </remarks>
    public async Task ResyncAsync(IReadOnlySet<string> activeSessions, CancellationToken ct)
    {
        if (_server is not { } server || _status is HarnessSessionStatus.Stopping or HarnessSessionStatus.Stopped)
            return;

        var wasRunning = _status is HarnessSessionStatus.Running;
        var running = activeSessions.Contains(ResumeToken);
        if (!wasRunning && !running)
            return;

        LogResync(_logger, InstanceId, wasRunning, running);
        var page = await server.Client.GetMessagesAsync(ResumeToken, ResyncMessages, cursor: null, ct).ConfigureAwait(false);
        foreach (var message in (page.Data ?? []).Reverse())
        {
            foreach (var harnessEvent in _mapper.MapMessage(message))
                _events.Writer.TryWrite(harnessEvent);
        }

        if (running)
        {
            var forms = await server.Client.GetFormsAsync(ResumeToken, ct).ConfigureAwait(false);
            _questions.Clear();
            foreach (var form in forms)
                RememberQuestion(form);

            if (!wasRunning)
                _events.Writer.TryWrite(_mapper.Status(ActivityStatuses.Busy));
            foreach (var harnessEvent in _mapper.MapPendingQuestions(forms))
                _events.Writer.TryWrite(harnessEvent);
            SetStatus(HarnessSessionStatus.Running);
        }
        else
        {
            _questions.Clear();
            _events.Writer.TryWrite(_mapper.Idle());
            SetStatus(HarnessSessionStatus.Idle);
        }
    }

    /// <inheritdoc />
    /// <remarks>A turn that was running is over: say so rather than leave the session looking busy.</remarks>
    public void OnServerStopped()
    {
        // V2 keeps background work in the server process: it stopped too, and no notice will say so.
        foreach (var delegation in _mapper.BackgroundDelegationsLost())
            Write(WorkEvents.Ended(OpenCode2Mapper.SubagentWork(delegation), WorkEndedReasons.Lost, _context.FleetSessionId));
        foreach (var harnessEvent in _mapper.BackgroundWorkEnded())
            Write(harnessEvent);

        // And whatever else was running: a subagent still working in the turn that just failed.
        foreach (var work in _work.Values.ToList())
            Write(WorkEvents.Ended(work, WorkEndedReasons.Lost, _context.FleetSessionId));

        if (_status is HarnessSessionStatus.Running)
        {
            _events.Writer.TryWrite(_mapper.Error(new OpenCode2ErrorInfo
            {
                Name = "ServerStopped",
                Message = "The OpenCode 2 server stopped during the turn.",
            }));
            _events.Writer.TryWrite(_mapper.Idle());
        }

        SetStatus(HarnessSessionStatus.Idle);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;
        _disposed = true;

        _server?.Detach(ResumeToken, this);
        _events.Writer.TryComplete();
        _attachLock.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task WaitForEventsCoreAsync(CancellationToken ct)
    {
        var server = await AttachedServerAsync(ct).ConfigureAwait(false);
        await server.WaitForEventsAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The server this session listens on, attaching to the owner's current one when the last one stopped.</summary>
    private async Task<OpenCode2Server> AttachedServerAsync(CancellationToken ct)
    {
        if (_server is { IsRunning: true } current)
        {
            current.Touch();
            return current;
        }

        await _attachLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_server is { IsRunning: true } attached)
                return attached;

            var server = await _servers(ct).ConfigureAwait(false);
            if (await server.Client.GetSessionAsync(ResumeToken, ct).ConfigureAwait(false) is null)
                throw new InvalidOperationException($"OpenCode 2 has no session {ResumeToken} any more.");

            Attach(server);
            LogReattached(_logger, InstanceId, ResumeToken, server.ProcessId ?? 0);
            return server;
        }
        finally
        {
            _attachLock.Release();
        }
    }

    private void Attach(OpenCode2Server server)
    {
        _server?.Detach(ResumeToken, this);
        server.Attach(ResumeToken, this);
        _server = server;
    }

    /// <summary>
    /// Switches the V2 session to the agent and model a prompt names, where they differ from what it has. A prompt that
    /// names no model gets its agent's own (else the folder's default): V2 would keep whatever model the session has,
    /// and a new session has none, so V2 would pick one itself. Fleet's effort is the model's variant.
    /// </summary>
    private async Task ApplyChoicesAsync(
        OpenCode2Server server,
        string? agent,
        string? providerId,
        string? modelId,
        string? effort,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(agent) && agent != _agent)
        {
            await server.Client.SwitchAgentAsync(ResumeToken, agent, ct).ConfigureAwait(false);
            _agent = agent;
        }

        var fallback = string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(modelId)
            ? await DefaultModelAsync(server, ct).ConfigureAwait(false)
            : null;
        if (ChosenModel(fallback, providerId, modelId, effort) is { } model && !SameModel(model, _model))
        {
            await server.Client.SwitchModelAsync(ResumeToken, model, ct).ConfigureAwait(false);
            _model = model;
        }
    }

    /// <summary>The model the session's agent gets when a prompt names none; <see langword="null"/> leaves it to V2.</summary>
    private async Task<OpenCode2ModelRef?> DefaultModelAsync(OpenCode2Server server, CancellationToken ct)
    {
        try
        {
            return await OpenCode2Catalog.ReadDefaultModelAsync(server, _context.WorkingDirectory, _agent, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            LogDefaultModelReadFailed(_logger, InstanceId, ex);
            return null;
        }
    }

    /// <summary>
    /// The model a prompt runs on: the one it names, else <paramref name="fallback"/>; <see langword="null"/> when there
    /// is neither. Effort picks the variant, else the fallback's own.
    /// </summary>
    internal static OpenCode2ModelRef? ChosenModel(OpenCode2ModelRef? fallback, string? providerId, string? modelId, string? effort)
    {
        var variant = string.IsNullOrWhiteSpace(effort) ? null : effort;
        if (!string.IsNullOrWhiteSpace(providerId) && !string.IsNullOrWhiteSpace(modelId))
            return new OpenCode2ModelRef { ProviderId = providerId, Id = modelId, Variant = variant };

        return fallback is null ? null : fallback with { Variant = variant ?? fallback.Variant };
    }

    /// <summary>Same model and variant; V2 reports a model selected without one as variant <c>default</c>.</summary>
    internal static bool SameModel(OpenCode2ModelRef model, OpenCode2ModelRef? current)
        => current is not null
            && model.ProviderId == current.ProviderId
            && model.Id == current.Id
            && (model.Variant ?? DefaultVariant) == (current.Variant ?? DefaultVariant);

    private const string DefaultVariant = "default";

    private void RememberQuestion(OpenCode2Form form)
    {
        if (form is { IsQuestion: true, Id: not null, ToolCallId: { } callId })
            _questions[callId] = form;
    }

    private void Forget(string formId)
    {
        foreach (var (callId, form) in _questions)
        {
            if (form.Id == formId)
                _questions.TryRemove(callId, out _);
        }
    }

    /// <summary>The question form for <paramref name="requestId"/> (a tool call or form id), from V2 when Fleet hasn't seen it.</summary>
    private async Task<OpenCode2Form> FindQuestionAsync(OpenCode2Server server, string requestId, CancellationToken ct)
    {
        if (Lookup() is { } known)
            return known;

        foreach (var form in await server.Client.GetFormsAsync(ResumeToken, ct).ConfigureAwait(false))
            RememberQuestion(form);
        return Lookup() ?? throw new KeyNotFoundException("OpenCode 2 has no open question for this answer any more.");

        OpenCode2Form? Lookup()
            => _questions.TryGetValue(requestId, out var byCall) ? byCall
                : _questions.Values.FirstOrDefault(f => f.Id == requestId);
    }

    /// <summary>
    /// The form reply: field <c>i</c> answers question <c>i</c>. A chosen label is sent as its option's value, and
    /// anything else as typed (the field allows free text). A multiselect field takes the list, any other field one value.
    /// </summary>
    internal static Dictionary<string, JsonElement> FormAnswer(OpenCode2Form form, IReadOnlyList<IReadOnlyList<string>> answers)
    {
        var reply = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var fields = form.Fields ?? [];
        for (var i = 0; i < fields.Count && i < answers.Count; i++)
        {
            var field = fields[i];
            var values = answers[i]
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => field.Options?.FirstOrDefault(o => o.Label == v)?.Value ?? v)
                .ToList();
            if (values.Count == 0)
                continue;

            reply[field.Key] = field.Type == "multiselect"
                ? JsonSerializer.SerializeToElement(values, OpenCode2JsonContext.Default.ListString)
                : JsonSerializer.SerializeToElement(string.Join(", ", values), OpenCode2JsonContext.Default.String);
        }

        return reply;
    }

    /// <inheritdoc />
    /// <remarks>
    /// V2 keeps the rules on the session, so they're set for the level when it changes; Fleet answers whatever V2 still
    /// asks (a subagent's child keeps its agent's rules). The workflow step tool stays denied where it was.
    /// </remarks>
    public async Task ApplyPermissionsAsync(PermissionPolicy policy, CancellationToken ct)
    {
        _permissions.Policy = policy;
        if (_rulesLevel == policy.Level)
            return;

        var server = await AttachedServerAsync(ct).ConfigureAwait(false);
        var info = await server.Client.GetSessionAsync(ResumeToken, ct).ConfigureAwait(false);
        if (info is null)
            return;

        // A subagent's child session keeps the rules its agent gave it; Fleet answers its asks all the same.
        if (!string.IsNullOrEmpty(info.ParentID))
        {
            _rulesLevel = policy.Level;
            return;
        }

        var hidesStepTool = (info.Permissions ?? []).Contains(OpenCode2HttpClient.DenyStepTool);
        // Fleet's browser, when attached, keeps the session's browser tools shown.
        var rules = OpenCode2HttpClient.BrowserAllowed(info.Permissions)
            ? OpenCode2HttpClient.WithBrowser(OpenCode2HttpClient.RulesFor(policy.Level, hidesStepTool), attached: true)
            : OpenCode2HttpClient.RulesFor(policy.Level, hidesStepTool);
        await server.Client.SetPermissionsAsync(ResumeToken, rules, ct).ConfigureAwait(false);
        _rulesLevel = policy.Level;
    }

    /// <inheritdoc />
    /// <remarks>"Don't ask again" is Fleet's to keep (<see cref="PermissionGate"/>); V2 is answered once.</remarks>
    public async Task ReplyToPermissionAsync(string requestId, string reply, string? message, CancellationToken ct)
    {
        var entry = _permissions.Pending.Get(requestId)
            ?? throw new KeyNotFoundException($"Permission request '{requestId}' isn't waiting for an answer.");
        if (reply == PermissionReplies.Always)
            _permissions.AllowFromNowOn(entry.Ask);

        var rejected = reply == PermissionReplies.Reject;
        var server = await AttachedServerAsync(ct).ConfigureAwait(false);
        await server.Client.ReplyToPermissionAsync(
            entry.HarnessSessionId,
            requestId,
            rejected ? PermissionReplies.Reject : PermissionReplies.Once,
            ct,
            rejected && !string.IsNullOrWhiteSpace(message) ? message : null).ConfigureAwait(false);
        SettlePermission(requestId, reply);
    }

    /// <summary>
    /// An ask from V2: <c>{ id, sessionID, action, resources, save, metadata, source: { id }, message }</c>. One the level
    /// allows, or the user said not to ask again about, is answered at once; any other waits for the user, and the session
    /// needs them. <paramref name="subagent"/>: it's from a subagent's child session Fleet hasn't attached yet, which the
    /// server hands its parent.
    /// </summary>
    internal void OnPermissionAsked(JsonElement data, bool subagent)
    {
        if (PermissionEvents.String(data, "id") is not { } requestId || PermissionEvents.String(data, "action") is not { } action)
            return;

        var harnessSessionId = PermissionEvents.String(data, "sessionID") ?? ResumeToken;
        var resources = PermissionEvents.Strings(data, "resources");
        if (_permissions.Decide(action, resources) is { } decision)
        {
            AnswerPermission(harnessSessionId, requestId, action, decision);
            return;
        }

        var metadata = data.TryGetProperty("metadata", out var m) ? m : default;
        var ask = new PermissionAsk
        {
            Id = requestId,
            SessionId = _context.FleetSessionId,
            Kind = PermissionKinds.Classify(action),
            Tool = action,
            Title = PermissionEvents.String(metadata, "command")
                ?? PermissionEvents.String(metadata, "filepath")
                ?? PermissionEvents.String(metadata, "path")
                ?? PermissionEvents.String(metadata, "url")
                ?? (resources.Count > 0 ? string.Join(", ", resources) : PermissionEvents.String(data, "message")),
            Detail = PermissionEvents.String(metadata, "diff"),
            Directory = PermissionEvents.String(metadata, "cwd") ?? PermissionEvents.String(metadata, "workdir"),
            Always = PermissionEvents.Strings(data, "save"),
            CallId = data.TryGetProperty("source", out var source) ? PermissionEvents.String(source, "id") : null,
            Subagent = subagent || harnessSessionId != ResumeToken ? "subagent" : null,
        };

        if (_permissions.Pending.Add(ask, harnessSessionId))
        {
            _events.Writer.TryWrite(PermissionEvents.Asked(ask, _context.FleetSessionId));
            _events.Writer.TryWrite(_mapper.Status(ActivityStatuses.WaitingInput));
        }
    }

    /// <summary>The ask no longer waits; once none does, the turn is working again.</summary>
    private void SettlePermission(string requestId, string reply)
    {
        if (_permissions.Pending.Remove(requestId) is not { } settled)
            return;
        _events.Writer.TryWrite(PermissionEvents.Replied(settled.Ask, reply, _context.FleetSessionId));
        if (!_permissions.Pending.Any)
            _events.Writer.TryWrite(_mapper.Status(ActivityStatuses.Busy));
    }

    /// <summary>Answers an ask Fleet decides itself: the level allows it, or asks are refused in a run nobody watches.</summary>
    private void AnswerPermission(string harnessSessionId, string requestId, string action, string decision)
    {
        if (_server is not { } server)
            return;

        LogPermissionAnswered(_logger, InstanceId, action, requestId, decision);
        _ = ReplyAsync();

        async Task ReplyAsync()
        {
            try
            {
                await server.Client.ReplyToPermissionAsync(
                    harnessSessionId,
                    requestId,
                    decision,
                    CancellationToken.None,
                    decision == PermissionReplies.Reject ? PermissionPolicy.UnattendedRejection : null).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                LogPermissionReplyFailed(_logger, InstanceId, requestId, ex);
            }
        }
    }

    private void SetStatus(HarnessSessionStatus status)
    {
        if (_status is not (HarnessSessionStatus.Stopping or HarnessSessionStatus.Stopped))
            _status = status;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "OpenCode 2 session {InstanceId} asked permission for {Action} ({RequestId}); answered '{Decision}' for its permission level")]
    private static partial void LogPermissionAnswered(ILogger logger, string instanceId, string action, string requestId, string decision);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't answer OpenCode 2 permission request {RequestId} for session {InstanceId}")]
    private static partial void LogPermissionReplyFailed(ILogger logger, string instanceId, string requestId, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "OpenCode 2 session {InstanceId} catching up after the event stream reconnected (was running: {WasRunning}, running: {Running})")]
    private static partial void LogResync(ILogger logger, string instanceId, bool wasRunning, bool running);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Sending a prompt to OpenCode 2 session {InstanceId}")]
    private static partial void LogPrompt(ILogger logger, string instanceId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Running command {Command} in OpenCode 2 session {InstanceId}")]
    private static partial void LogCommand(ILogger logger, string instanceId, string command);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Running a shell command in OpenCode 2 session {InstanceId}")]
    private static partial void LogShellCommand(ILogger logger, string instanceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A shell command in OpenCode 2 session {InstanceId} failed after V2 took it")]
    private static partial void LogShellCommandFailed(ILogger logger, string instanceId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the default model for OpenCode 2 session {InstanceId}; the prompt runs on the model the session has")]
    private static partial void LogDefaultModelReadFailed(ILogger logger, string instanceId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the inbox of OpenCode 2 session {InstanceId}; prompts it hasn't taken in yet are left out of the history")]
    private static partial void LogInboxReadFailed(ILogger logger, string instanceId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't check whether background work of OpenCode 2 session {InstanceId} still runs; it goes on showing as running")]
    private static partial void LogBackgroundCheckFailed(ILogger logger, string instanceId, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Interrupting OpenCode 2 session {InstanceId}")]
    private static partial void LogAbort(ILogger logger, string instanceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't interrupt OpenCode 2 session {InstanceId} while stopping it")]
    private static partial void LogStopInterruptFailed(ILogger logger, string instanceId, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "OpenCode 2 session {InstanceId} ({HarnessSessionId}) attached again, on server {ProcessId}")]
    private static partial void LogReattached(ILogger logger, string instanceId, string harnessSessionId, int processId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "OpenCode 2 session {InstanceId} put no user message in for command {Command} that Fleet saw")]
    private static partial void LogCommandMessageNotSeen(ILogger logger, string instanceId, string command);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Couldn't read the limits of model {ModelId} from OpenCode 2")]
    private static partial void LogModelLimitsUnavailable(ILogger logger, string modelId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't delete OpenCode 2 session {InstanceId} from its server")]
    private static partial void LogDeleteFailed(ILogger logger, string instanceId, Exception exception);
}

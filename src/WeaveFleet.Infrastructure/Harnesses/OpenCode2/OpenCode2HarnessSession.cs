using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Analytics;
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
    private readonly OpenCode2Delegations? _delegations;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _attachLock = new(1, 1);
    private readonly Channel<HarnessEvent> _events = Channel.CreateBounded<HarnessEvent>(new BoundedChannelOptions(1000)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = false,
        SingleWriter = false,
    });

    // The questions waiting on the user, by the tool call that asked: the client answers by call id.
    private readonly ConcurrentDictionary<string, OpenCode2Form> _questions = new(StringComparer.Ordinal);

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
    /// <param name="delegations">Records the session's subagent calls; none in tests that don't need them.</param>
    internal OpenCode2HarnessSession(
        string instanceId,
        OpenCode2SessionInfo info,
        OpenCode2SessionContext context,
        OpenCode2Server server,
        Func<CancellationToken, Task<OpenCode2Server>> servers,
        IAnalyticsCollector? analytics,
        OpenCode2Delegations? delegations,
        ILogger logger)
    {
        InstanceId = instanceId;
        ResumeToken = info.Id ?? throw new ArgumentException("The V2 session has no id.", nameof(info));
        _context = context;
        _servers = servers;
        _analytics = analytics;
        _delegations = delegations;
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
        await server.Client.PromptAsync(ResumeToken, text, options?.MessageId, PromptFiles(options?.Attachments), ct).ConfigureAwait(false);
    }

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
        return new MessagePage(
            OpenCode2History.ToHarnessMessages(messages.Reverse()),
            hasMore,
            hasMore ? page.Cursor!.Next : null);
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
    /// own. That id is read off V2's inbox (<see cref="OnEvent"/>), one command at a time.
    /// </summary>
    public async Task<string?> SendCommandAsync(CommandOptions options, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var server = await AttachedServerAsync(ct).ConfigureAwait(false);
        LogCommand(_logger, InstanceId, options.Command);

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

        // Read before mapping too: the mapper forgets a tool call once it ends.
        if (_delegations is not null && _mapper.TryReadDelegation(evt) is { } delegation)
            _delegations.Queue(delegation);

        if (OpenCode2Mapper.ReadUserMessageTakenIn(evt) is { } userMessageId && !_promptsNotTakenIn.TryRemove(userMessageId, out _))
            _commandMessage?.TrySetResult(userMessageId);

        switch (evt.Type)
        {
            case "session.execution.started":
                SetStatus(HarnessSessionStatus.Running);
                break;
            case "session.execution.succeeded" or "session.execution.failed" or "session.execution.interrupted":
                SetStatus(HarnessSessionStatus.Idle);
                break;
            case "form.created" when evt.Data.TryGetProperty("form", out var form) && form.ValueKind == JsonValueKind.Object:
                if (form.Deserialize(OpenCode2JsonContext.Default.OpenCode2Form) is { } created)
                    RememberQuestion(created);
                break;
            case "form.replied" or "form.cancelled" when evt.Data.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String:
                Forget(id.GetString()!);
                break;
            case "permission.asked":
                AnswerPermission(evt.Data);
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
            _events.Writer.TryWrite(harnessEvent);
    }

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
    /// Switches the V2 session to the agent and model a prompt names, where they differ from what it has. Fleet's effort
    /// is the model's variant; a prompt that names an effort but no model changes the variant of the model it has.
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

        if (ChosenModel(_model, providerId, modelId, effort) is { } model && !SameModel(model, _model))
        {
            await server.Client.SwitchModelAsync(ResumeToken, model, ct).ConfigureAwait(false);
            _model = model;
        }
    }

    /// <summary>The model a prompt asks for, or <see langword="null"/> when it leaves the session's as it is.</summary>
    internal static OpenCode2ModelRef? ChosenModel(OpenCode2ModelRef? current, string? providerId, string? modelId, string? effort)
    {
        var variant = string.IsNullOrWhiteSpace(effort) ? null : effort;
        if (!string.IsNullOrWhiteSpace(providerId) && !string.IsNullOrWhiteSpace(modelId))
            return new OpenCode2ModelRef { ProviderId = providerId, Id = modelId, Variant = variant };

        return variant is not null && current is { Id: not null, ProviderId: not null }
            ? current with { Variant = variant }
            : null;
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
        return Lookup() ?? throw new InvalidOperationException("OpenCode 2 has no open question for this answer any more.");

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

    /// <summary>
    /// Sessions are created allowing everything, so V2 asks only when something overrides that (a subagent's own
    /// rules, a plugin). Nobody is there to answer, so it's allowed once and logged, as Fleet does for OpenCode (1.x).
    /// </summary>
    private void AnswerPermission(JsonElement data)
    {
        if (data.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && _server is { } server)
        {
            var requestId = id.GetString()!;
            var action = data.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
            LogPermissionAllowed(_logger, InstanceId, action ?? "(unknown)", requestId);
            _ = ReplyAsync();

            async Task ReplyAsync()
            {
                try
                {
                    await server.Client.ReplyToPermissionAsync(ResumeToken, requestId, "once", CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    LogPermissionReplyFailed(_logger, InstanceId, requestId, ex);
                }
            }
        }
    }

    private void SetStatus(HarnessSessionStatus status)
    {
        if (_status is not (HarnessSessionStatus.Stopping or HarnessSessionStatus.Stopped))
            _status = status;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "OpenCode 2 session {InstanceId} asked permission for {Action} ({RequestId}); allowed once, since nobody can answer it")]
    private static partial void LogPermissionAllowed(ILogger logger, string instanceId, string action, string requestId);

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

    [LoggerMessage(Level = LogLevel.Information, Message = "Interrupting OpenCode 2 session {InstanceId}")]
    private static partial void LogAbort(ILogger logger, string instanceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't interrupt OpenCode 2 session {InstanceId} while stopping it")]
    private static partial void LogStopInterruptFailed(ILogger logger, string instanceId, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "OpenCode 2 session {InstanceId} ({HarnessSessionId}) attached again, on server {ProcessId}")]
    private static partial void LogReattached(ILogger logger, string instanceId, string harnessSessionId, int processId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "OpenCode 2 session {InstanceId} put no user message in for command {Command} that Fleet saw")]
    private static partial void LogCommandMessageNotSeen(ILogger logger, string instanceId, string command);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't delete OpenCode 2 session {InstanceId} from its server")]
    private static partial void LogDeleteFailed(ILogger logger, string instanceId, Exception exception);
}

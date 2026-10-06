using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Analytics;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.FleetTools;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Identity;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.Infrastructure.Harnesses.ClaudeCode;

/// <summary>
/// Wraps a Claude Code CLI session for a single Fleet session.
/// Implements <see cref="IHarnessSession"/> with full database persistence.
/// Each instance owns its own message persistence — the relay is not involved.
/// <para>
/// One <c>claude</c> process runs the session's prompts one after another, so the work an agent leaves running in the
/// background (a <c>Bash</c> call with <c>run_in_background</c>, a <c>Monitor</c>, a background subagent) carries on
/// after its turn. When that work finishes Claude Code starts a turn by itself, which Fleet shows like any other.
/// The process ends when the session stops, when Fleet shuts down, and once it has been idle for
/// <see cref="ClaudeCodeOptions.IdleShutdownSeconds"/> with no background work; the next prompt starts another with
/// <c>--resume</c>.
/// </para>
/// <para>
/// That work is Fleet's running work (<see cref="ClaudeCodeTasks"/>, reported as <c>work.*</c> events). A subagent's
/// own steps (the lines that carry its call's <c>parent_tool_use_id</c>) go to a hidden child session of their own,
/// nested under the conversation that started the subagent. A child session is read-only: Claude Code can't prompt a
/// subagent on its own, so its instance (<c>readOnlyChild</c>) only reads what this one saved.
/// </para>
/// </summary>
internal sealed class ClaudeCodeHarnessSession : IHarnessSession
{
    private static readonly Action<ILogger, string, Exception?> LogSendPrompt =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(1, "SendPrompt"),
            "Sending prompt to ClaudeCode instance {InstanceId}.");

    private static readonly Action<ILogger, string, Exception?> LogAbort =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(2, "Abort"),
            "Aborting ClaudeCode instance {InstanceId}.");

    private static readonly Action<ILogger, string, Exception?> LogStop =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(3, "Stop"),
            "Stopping ClaudeCode instance {InstanceId}.");

    private static readonly Action<ILogger, string, Exception?> LogPersistFailed =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(4, "PersistFailed"),
            "Failed to persist message for ClaudeCode session {SessionId}.");

    private static readonly Action<ILogger, string, Exception?> LogSessionId =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(5, "SessionId"),
            "ClaudeCode session ID: {SessionId}.");

    private static readonly Action<ILogger, string, Exception?> LogPumpFailed =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(6, "PumpFailed"),
            "Reading output from ClaudeCode instance {InstanceId} failed.");

    private static readonly Action<ILogger, string, string, int, string, Exception?> LogWorkLost =
        LoggerMessage.Define<string, string, int, string>(LogLevel.Warning, new EventId(7, "WorkLost"),
            "Claude Code for session {SessionId} ended ({Reason}) with {Count} background task(s) still running; that work is lost: {Tasks}");

    private static readonly Action<ILogger, string, string, Exception?> LogProcessEnded =
        LoggerMessage.Define<string, string>(LogLevel.Information, new EventId(8, "ProcessEnded"),
            "Claude Code for session {SessionId} ended: {Reason}.");

    private static readonly Action<ILogger, string, string, string, Exception?> LogRequestRefused =
        LoggerMessage.Define<string, string, string>(LogLevel.Warning, new EventId(9, "RequestRefused"),
            "Claude Code for session {SessionId} didn't do '{Request}': {Error}");

    private static readonly Action<ILogger, string, string, string, Exception?> LogBackgroundWork =
        LoggerMessage.Define<string, string, string>(LogLevel.Debug, new EventId(10, "BackgroundWork"),
            "Claude Code for session {SessionId}: {Change}; running in the background: {Tasks}");

    private static readonly Action<ILogger, string, Exception?> LogWakeTurn =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(11, "WakeTurn"),
            "Claude Code for session {SessionId} started a turn by itself.");

    private static readonly Action<ILogger, string, string, Exception?> LogChildFailed =
        LoggerMessage.Define<string, string>(LogLevel.Warning, new EventId(12, "ChildFailed"),
            "Claude Code for session {SessionId}: couldn't make a child session for subagent call {CallId}; its steps aren't kept.");

    private static readonly Action<ILogger, string, Exception?> LogFleetToolsUnread =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(13, "FleetToolsUnread"),
            "Claude Code for session {SessionId}: couldn't read which of Fleet's tools it gets; it keeps the ones it has.");

    private readonly string _workingDirectory;
    private readonly ClaudeCodeOptions _config;
    private readonly IReadOnlyDictionary<string, string> _environmentVariables;
    private readonly TimeSpan _shutdownTimeout;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly string _fleetSessionId;
    private readonly string _ownerUserId;
    private readonly ILogger<ClaudeCodeHarnessSession> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IAnalyticsCollector? _analyticsCollector;
    private readonly string? _projectId;
    private readonly string? _projectName;

    // Bounded channel for event delivery (DropOldest so slow consumers don't stall the pump)
    private readonly Channel<HarnessEvent> _eventChannel =
        Channel.CreateBounded<HarnessEvent>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            // The pump writes, and so does an answer to an ask.
            SingleWriter = false,
            SingleReader = false,
        });

    // Held while a prompt is being sent, and by the idle shutdown, so neither starts or stops the process under the other.
    private readonly SemaphoreSlim _promptLock = new(1, 1);

    // Guards the turn, the process and the background work, which the pump, prompts, Stop and the timers all change.
    private readonly Lock _gate = new();

    // The running prompt's messages (see Conversation), started afresh with each turn.
    private readonly Conversation _root;

    // The session's permission level, the asks waiting on the user (with the input each tool call asked with, which the
    // answer sends back), and what they said not to ask again about.
    private readonly PermissionGate _permissions = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Text.Json.JsonElement> _askInputs = new(StringComparer.Ordinal);

    // The work the agent left running: what Fleet shows, whether the process may stop when idle, and what was lost when
    // it ends anyway.
    private readonly ClaudeCodeTasks _tasks = new();

    // A subagent's child session: Claude Code can't prompt it, so this instance only reads what its parent saved.
    private readonly bool _readOnlyChild;

    // The bridge token each process gets for calling Fleet (FLEET_URL), and where Fleet listens; none in tests.
    private readonly ClaudeCodeBridgeTokenRegistry? _bridgeTokens;
    private readonly Func<string?> _fleetUrl;
    private FleetToolCallRecords? _fleetToolCalls;

    // The models the picker offers; Claude Code's own list, from the runtime.
    private readonly ClaudeCodeCatalog? _catalog;

    // AskUserQuestion calls waiting on the user, by tool call: Claude Code's request, and the questions it asked.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, PendingQuestion> _questions = new(StringComparer.Ordinal);

    // What the user answered each question call with, kept with the call once it returns.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<IReadOnlyList<string>>> _answers = new(StringComparer.Ordinal);

    private string? _claudeSessionId;    // captured from init message, used for --resume
    // The memory notes from the session's first prompt, sent with every prompt after it: a system prompt that changes
    // between --resume runs can't reuse the conversation's prompt cache.
    private string? _memoryNotes;
    private string? _modelId;             // captured from init or result messages

    // The context window: the last call reported (its message id; Claude Code repeats a call's usage on each of its
    // lines), and the model's limits from the last result line.
    private string? _reportedContextCallId;
    private int? _contextWindow;
    private int? _maxOutputTokens;
    private HarnessSessionStatus _status = HarnessSessionStatus.Idle;
    private ClaudeCodeProcessManager? _process;
    private ProcessSettings? _processSettings;
    private Task _pump = Task.CompletedTask;
    private Turn? _turn;
    private ITimer? _idleTimer;
    private volatile bool _aborting;
    private bool _disposed;

    /// <summary>Initialises the instance with all required dependencies.</summary>
    public ClaudeCodeHarnessSession(
        string instanceId,
        string fleetSessionId,
        string workingDirectory,
        ClaudeCodeOptions config,
        IReadOnlyDictionary<string, string> environmentVariables,
        TimeSpan shutdownTimeout,
        IServiceScopeFactory scopeFactory,
        ILogger<ClaudeCodeHarnessSession> logger,
        ILoggerFactory loggerFactory,
        string ownerUserId,
        IAnalyticsCollector? analyticsCollector = null,
        string? projectId = null,
        string? projectName = null,
        string? claudeSessionId = null,
        bool readOnlyChild = false,
        ClaudeCodeBridgeTokenRegistry? bridgeTokens = null,
        Func<string?>? fleetUrl = null,
        ClaudeCodeCatalog? catalog = null)
    {
        InstanceId = instanceId;
        _fleetSessionId = fleetSessionId;
        _workingDirectory = workingDirectory;
        _config = config;
        _environmentVariables = environmentVariables;
        _shutdownTimeout = shutdownTimeout;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _ownerUserId = ownerUserId;
        _analyticsCollector = analyticsCollector;
        _projectId = projectId;
        _projectName = projectName;
        _claudeSessionId = claudeSessionId;
        _readOnlyChild = readOnlyChild;
        _bridgeTokens = bridgeTokens;
        _fleetUrl = fleetUrl ?? (() => null);
        _catalog = catalog;
        _root = new Conversation(fleetSessionId);
        ChildSessions = MakeChildSessionAsync;
    }

    /// <summary>The clock for the idle shutdown and the turn timeout; the system's unless a test says otherwise.</summary>
    internal TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>The background work Claude Code has reported and not yet reported finished, by task id.</summary>
    internal IReadOnlyCollection<string> BackgroundWork
    {
        get
        {
            lock (_gate)
                return [.. _tasks.Background.Keys];
        }
    }

    /// <summary>
    /// Makes (or finds) the hidden Fleet session a subagent's steps go to: under Fleet session <c>parent</c>, for the
    /// subagent called with tool call <c>callId</c>, titled <c>title</c>. Returns its id, or null when it can't be made.
    /// Fleet's <see cref="SessionOrchestrator.EnsureDelegatedChildSessionAsync"/> unless a test says otherwise.
    /// </summary>
    internal Func<string, string, string, Task<string?>> ChildSessions { get; init; }

    /// <inheritdoc />
    public string InstanceId { get; }

    /// <inheritdoc />
    public int? ProcessId => _process?.ProcessId;

    /// <inheritdoc />
    public string HarnessType => "claude-code";

    /// <inheritdoc />
    public string? ResumeToken => _claudeSessionId;

    /// <inheritdoc />
    public HarnessSessionStatus Status => _status;

    // -----------------------------------------------------------------------
    // IHarnessSession
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    public async Task SendPromptAsync(string text, PromptOptions? options, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_readOnlyChild)
        {
            throw new InvalidOperationException(
                "This is a Claude Code subagent's session: Claude Code can't prompt a subagent on its own. Ask the session that started it.");
        }

        // A message for the running turn goes straight in: Claude Code reads it at the agent's next step.
        if (options?.Delivery == PromptDelivery.Steer && await TrySteerAsync(text, options, ct).ConfigureAwait(false))
            return;

        await _promptLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            LogSendPrompt(_logger, InstanceId, null);

            // 1. One turn at a time: a prompt sent mid-turn (Fleet's, or one Claude Code started by itself) waits for
            //    that turn to end.
            var turn = await BeginPromptedTurnAsync(ct).ConfigureAwait(false);
            try
            {
                // The conversation is read back from Fleet's database, so the prompt is saved there, under
                // the id Fleet already showed it with.
                await PersistUserPromptAsync(text, options, steered: false).ConfigureAwait(false);

                // 2. Emit session busy status
                var busyEvent = ClaudeCodeMapper.CreateSessionStatusEvent(_fleetSessionId, "busy");
                await _eventChannel.Writer.WriteAsync(busyEvent, ct).ConfigureAwait(false);

                // 3. Send it to the session's claude process, starting one if there's none, with Fleet's notes to the
                //    model ahead of it.
                _memoryNotes ??= options?.MemoryNotes;
                var line = ClaudeCodeInput.UserMessage(text, options?.ModelNotes, options?.Attachments);
                var process = await EnsureProcessAsync(options, ct).ConfigureAwait(false);
                if (!await SendToAsync(process, turn, line, ct).ConfigureAwait(false))
                {
                    // It exited between turns; a new one resumes the conversation.
                    await StopProcessAsync(process, "it stopped reading its input").ConfigureAwait(false);
                    process = await EnsureProcessAsync(options, ct).ConfigureAwait(false);
                    if (!await SendToAsync(process, turn, line, ct).ConfigureAwait(false))
                        throw new InvalidOperationException("Claude Code exited before it read the prompt.");
                }
            }
            catch
            {
                bool ended;
                lock (_gate)
                    ended = EndTurn(turn);
                if (ended)
                {
                    await _eventChannel.Writer.WriteAsync(ClaudeCodeMapper.CreateSessionIdleEvent(_fleetSessionId), CancellationToken.None)
                        .ConfigureAwait(false);
                }

                throw;
            }
        }
        finally
        {
            _promptLock.Release();
        }
    }

    /// <summary>
    /// Writes a prompt into the running turn. Claude Code reads it when the agent's current step ends (a tool call
    /// returns), in the same turn, and goes on from there; the command running meanwhile isn't stopped (Fleet doesn't
    /// send the <c>priority: now</c> that would). Claude Code doesn't write the message back, so it's saved here, under
    /// the id Fleet showed it with when it was sent. False when no turn is running on a live process: the prompt then
    /// starts one of its own.
    /// </summary>
    private async Task<bool> TrySteerAsync(string text, PromptOptions options, CancellationToken ct)
    {
        ClaudeCodeProcessManager? process;
        lock (_gate)
            process = _turn?.Process is { IsRunning: true } running && ReferenceEquals(running, _process) ? running : null;
        if (process is null)
            return false;

        LogSendPrompt(_logger, InstanceId, null);
        var line = ClaudeCodeInput.UserMessage(text, options.ModelNotes, options.Attachments);
        if (!await process.WriteLineAsync(line, ct).ConfigureAwait(false))
            return false;

        // Had the turn just ended, Claude Code starts one with it by itself, which Fleet follows like any other.
        await PersistUserPromptAsync(text, options, steered: true).ConfigureAwait(false);
        return true;
    }

    /// <summary>Waits for a running turn to end, then starts Fleet's.</summary>
    private async Task<Turn> BeginPromptedTurnAsync(CancellationToken ct)
    {
        while (true)
        {
            Task running;
            lock (_gate)
            {
                if (_status is HarnessSessionStatus.Stopping or HarnessSessionStatus.Stopped)
                    throw new ObjectDisposedException(nameof(ClaudeCodeHarnessSession), "The session has stopped.");
                if (_turn is null)
                    return BeginTurn(process: null);
                running = _turn.Done.Task;
            }

            await running.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Writes the prompt to <paramref name="process"/>; from then on the turn is that process's.</summary>
    private async Task<bool> SendToAsync(ClaudeCodeProcessManager process, Turn turn, string line, CancellationToken ct)
    {
        lock (_gate)
            turn.Process ??= process;
        if (await process.WriteLineAsync(line, ct).ConfigureAwait(false))
            return true;

        lock (_gate)
        {
            if (ReferenceEquals(turn.Process, process))
                turn.Process = null;
        }

        return false;
    }

    /// <summary>
    /// The session's claude process, set up for this prompt: a running one switches model, effort and permission mode
    /// with control requests; one that can't is replaced. A new one resumes the conversation.
    /// </summary>
    private async Task<ClaudeCodeProcessManager> EnsureProcessAsync(PromptOptions? options, CancellationToken ct)
    {
        var policy = _permissions.Policy;
        ClaudeCodeProcessManager? process;
        ProcessSettings? current;
        lock (_gate)
        {
            process = _process;
            current = _processSettings;
        }

        var wanted = new ProcessSettings(
            PermissionMode: PermissionModeFor(policy),
            Model: options?.ModelId ?? _config.DefaultModel,
            Effort: options?.Effort,
            FleetTools: await ReadFleetToolsAsync(current?.FleetTools).ConfigureAwait(false));

        if (process is { IsRunning: true } && current is not null)
        {
            // Claude Code lists an MCP server's tools when it starts, so a switch that changed since (memory, messages
            // between sessions, Settings → Browser) takes a new process, which resumes the conversation. Not while work
            // it left running would end with it: until that's done, the process keeps the tools it has.
            if (current.FleetTools != wanted.FleetTools)
            {
                bool working;
                lock (_gate)
                    working = _tasks.HasBackgroundWork;
                if (!working)
                {
                    await StopProcessAsync(process, "Fleet's tools for it changed").ConfigureAwait(false);
                    return await StartProcessAsync(wanted, ct).ConfigureAwait(false);
                }

                wanted = wanted with { FleetTools = current.FleetTools };
            }

            // A prompt that names no model or effort keeps what the process runs.
            var model = wanted.Model ?? current.Model;
            var effort = wanted.Effort ?? current.Effort;
            if (await SwitchAsync(process, current.PermissionMode, wanted.PermissionMode,
                    id => ClaudeCodeInput.SetPermissionMode(id, wanted.PermissionMode), ct).ConfigureAwait(false)
                && await SwitchAsync(process, current.Model, model,
                    id => ClaudeCodeInput.SetModel(id, model!), ct).ConfigureAwait(false)
                && await SwitchAsync(process, current.Effort, effort,
                    id => ClaudeCodeInput.SetEffort(id, effort), ct).ConfigureAwait(false))
            {
                lock (_gate)
                    _processSettings = wanted with { Model = model, Effort = effort };
                return process;
            }

            await StopProcessAsync(process, "it couldn't switch to the prompt's model, effort or permission settings").ConfigureAwait(false);
        }
        else if (process is not null)
        {
            await StopProcessAsync(process, "it had exited").ConfigureAwait(false);
        }

        return await StartProcessAsync(wanted, ct).ConfigureAwait(false);
    }

    /// <summary>Asks the running process to change a setting; true when it's already that, or it did.</summary>
    private async Task<bool> SwitchAsync(
        ClaudeCodeProcessManager process, string? from, string? to, Func<string, string> request, CancellationToken ct)
    {
        if (to is null || string.Equals(from, to, StringComparison.Ordinal))
            return true;

        var requestId = $"fleet-{Guid.NewGuid():N}";
        var line = request(requestId);
        var answer = await process.RequestAsync(requestId, line, _shutdownTimeout, ct).ConfigureAwait(false);
        if (answer?.Subtype == "success")
            return true;

        LogRequestRefused(_logger, _fleetSessionId, line, answer?.Error ?? "no answer", null);
        return false;
    }

    private async Task<ClaudeCodeProcessManager> StartProcessAsync(ProcessSettings settings, CancellationToken ct)
    {
        var processManager = new ClaudeCodeProcessManager(
            _loggerFactory.CreateLogger<ClaudeCodeProcessManager>());

        // The agent reaches Fleet's API under a path that names this process (FLEET_URL), so its calls are this session's
        // whatever Fleet binds to. The token is the process's own: it stops working when the process ends.
        var environment = new Dictionary<string, string>(_environmentVariables, StringComparer.Ordinal);
        if (_bridgeTokens is not null)
        {
            var bridgeToken = _bridgeTokens.Issue(_fleetSessionId, _ownerUserId);
            processManager.BridgeToken = bridgeToken;
            environment["FLEET_BRIDGE_TOKEN"] = bridgeToken;
            environment["FLEET_HARNESS_SESSION_ID"] = _fleetSessionId;
            if (_fleetUrl() is { Length: > 0 } fleetUrl)
                environment["FLEET_URL"] = $"{fleetUrl.TrimEnd('/')}{WeaveFleet.Application.Sessions.SessionMessages.AgentPathPrefix}/{bridgeToken}";
        }

        var procOptions = new ClaudeCodeProcessOptions
        {
            BinaryPath = _config.BinaryPath,
            WorkingDirectory = _workingDirectory,
            SessionId = _claudeSessionId,  // null for the session's first process
            Model = settings.Model,
            Effort = settings.Effort,
            PermissionMode = settings.PermissionMode,
            // Always: at "Allow everything" Fleet answers every ask at once, but without it Claude Code takes
            // AskUserQuestion away from the agent, since nothing could answer it.
            AsksForPermission = true,
            AllowedTools = _config.AllowedTools,
            MaxTurns = _config.MaxTurns,
            MaxBudgetUsd = _config.MaxBudgetUsd,
            AppendSystemPrompt = _memoryNotes,
            EnvironmentVariables = environment,
        };

        // Fleet's own tools, from Fleet's MCP server under the process's FLEET_URL. Each is allowed up front (Fleet allows
        // them at every level), except fleet_app_start, which asks as a shell command does.
        if (settings.FleetTools is { } fleetTools && environment.ContainsKey("FLEET_URL"))
        {
            procOptions = procOptions with
            {
                McpConfig = ClaudeCodeFleetTools.McpConfig,
                AllowedTools = [.. procOptions.AllowedTools, .. ClaudeCodeFleetTools.AllowedWithoutAsking(fleetTools)],
            };
        }

        StreamReader stdout;
        try
        {
            stdout = await processManager.StartAsync(procOptions, ct).ConfigureAwait(false);
        }
        catch
        {
            RevokeBridgeToken(processManager);
            await processManager.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        lock (_gate)
        {
            _process = processManager;
            _processSettings = settings;
            // Background pump — reads every turn this process runs, until it exits.
            _pump = Task.Run(() => PumpStdoutAsync(stdout, processManager), CancellationToken.None);
        }

        return processManager;
    }

    /// <summary>
    /// Ends <paramref name="process"/> and waits for its output to be read to the end. Background work still running
    /// in it is lost; the log says so, with <paramref name="reason"/>.
    /// </summary>
    private async Task StopProcessAsync(ClaudeCodeProcessManager process, string reason)
    {
        Task pump;
        lock (_gate)
        {
            process.StopReason ??= reason;
            pump = ReferenceEquals(_process, process) ? _pump : Task.CompletedTask;
        }

        await process.StopAsync(_shutdownTimeout).ConfigureAwait(false);
        try
        {
            await pump.WaitAsync(_shutdownTimeout + TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Its output never closed; the process is gone either way.
        }

        IReadOnlyList<ClaudeCodeTasks.Change> lost = [];
        lock (_gate)
        {
            if (ReferenceEquals(_process, process))
                lost = ForgetProcess();
        }

        // Its output never closed, so nothing settled its work.
        await ReportWorkAsync(lost, children: null).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<string?> SendCommandAsync(CommandOptions options, CancellationToken ct)
    {
        // Sanitize arguments: collapse newlines to spaces to prevent prompt injection
        var promptText = CommandFormatting.FormatCommandPrompt(options);

        // Claude Code expands the command itself; the prompt Fleet keeps is "/name arguments", under Fleet's id.
        var promptOptions = new PromptOptions { Agent = options.Agent, ModelId = options.ModelId, MessageId = options.MessageId };

        await SendPromptAsync(promptText, promptOptions, ct).ConfigureAwait(false);
        return options.MessageId;
    }

    /// <summary>
    /// Compacts the session's context with Claude Code's own <c>/compact</c>, sent as a prompt: Claude Code runs it as a
    /// turn of its own, and reports the compaction's start and end on its way.
    /// </summary>
    public Task CompactAsync(CompactOptions options, CancellationToken ct)
        => SendPromptAsync("/compact", new PromptOptions { ModelId = options.ModelId }, ct);

    /// <inheritdoc />
    public async Task<MessagePage> GetMessagesAsync(MessageQuery? query, CancellationToken ct)
    {
        // Claude Code has no "get messages" API — always read from database
        using var userScope = BackgroundUserContext.BeginScope(_ownerUserId);
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMessageRepository>();

        var persisted = await repo.GetBySessionAsync(
            _fleetSessionId,
            query?.Limit ?? 50,
            query?.Before).ConfigureAwait(false);

        var messages = MessagePersistenceService.ToHarnessMessages(persisted);
        bool hasMore = query?.Limit.HasValue == true && persisted.Count >= query.Limit.Value;

        return new MessagePage(messages, hasMore);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<HarnessEvent> SubscribeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var evt in _eventChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return evt;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Stops the running turn with Claude Code's <c>interrupt</c> request, so work the agent left running in the
    /// background carries on. Only if that doesn't end the turn is the process killed, and that work with it.
    /// </remarks>
    public async Task AbortAsync(CancellationToken ct)
    {
        LogAbort(_logger, InstanceId, null);

        Turn? turn;
        ClaudeCodeProcessManager? process;
        lock (_gate)
        {
            turn = _turn;
            process = turn?.Process ?? _process;
        }

        // No turn running: background work isn't the user's to stop here.
        if (turn is null)
            return;

        _aborting = true;
        if (process is { IsRunning: true })
        {
            var requestId = $"fleet-{Guid.NewGuid():N}";
            var answer = await process.RequestAsync(requestId, ClaudeCodeInput.Interrupt(requestId), _shutdownTimeout, ct)
                .ConfigureAwait(false);
            if (answer?.Subtype == "success" && await EndsWithinAsync(turn, _shutdownTimeout).ConfigureAwait(false))
                return;

            LogRequestRefused(_logger, _fleetSessionId, "interrupt", answer?.Error ?? "the turn didn't end", null);
            await StopProcessAsync(process, "an interrupt didn't stop its turn").ConfigureAwait(false);
        }

        // Nothing is left to end the turn.
        bool ended;
        lock (_gate)
            ended = EndTurn(turn);
        if (ended)
            _eventChannel.Writer.TryWrite(ClaudeCodeMapper.CreateSessionIdleEvent(_fleetSessionId));
    }

    private static async Task<bool> EndsWithinAsync(Turn turn, TimeSpan timeout)
    {
        try
        {
            await turn.Done.Task.WaitAsync(timeout).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Applies from the next prompt: Fleet switches the running process's permission mode then, or starts another
    /// (resuming the conversation) when Claude Code can't switch, e.g. into <c>bypassPermissions</c>, or when Fleet's
    /// permission prompts are needed and the process was started without them.
    /// </remarks>
    public Task ApplyPermissionsAsync(PermissionPolicy policy, CancellationToken ct)
    {
        _permissions.Policy = policy;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// "Don't ask again" is Fleet's to keep (<see cref="PermissionGate"/>): Claude Code's own session rules end with its
    /// process, which can be replaced between prompts.
    /// </remarks>
    public async Task ReplyToPermissionAsync(string requestId, string reply, string? message, CancellationToken ct)
    {
        var entry = _permissions.Pending.Get(requestId)
            ?? throw new KeyNotFoundException($"Permission request '{requestId}' isn't waiting for an answer.");
        if (reply == PermissionReplies.Always)
            _permissions.AllowFromNowOn(entry.Ask);

        var line = reply == PermissionReplies.Reject
            ? ClaudeCodeInput.Deny(requestId, string.IsNullOrWhiteSpace(message)
                ? "The user refused this tool call."
                : $"The user refused this tool call and said: {message}")
            : ClaudeCodeInput.Allow(requestId, _askInputs.GetValueOrDefault(requestId));

        if (_process is not { } process || !await process.WriteLineAsync(line, ct).ConfigureAwait(false))
        {
            SettlePermission(requestId, PermissionReplies.Gone);
            throw new KeyNotFoundException($"Permission request '{requestId}' isn't waiting for an answer: Claude Code has stopped.");
        }

        SettlePermission(requestId, reply);
    }

    /// <summary>Whether anything still waits on the user: a permission ask or a question.</summary>
    private bool WaitsOnUser => _permissions.Pending.Any || !_questions.IsEmpty;

    /// <summary>
    /// Claude Code's mode for a policy: it allows reading itself, and asks Fleet about the rest. At
    /// <see cref="PermissionLevels.All"/> it's the configured mode, which bypasses asking.
    /// </summary>
    internal string PermissionModeFor(PermissionPolicy policy) => policy.Level switch
    {
        PermissionLevels.Ask => "default",
        PermissionLevels.Edits => "acceptEdits",
        _ => _config.PermissionMode,
    };

    /// <summary>
    /// A <c>can_use_tool</c> ask. One the level allows, or the user said not to ask again about, is answered at once; any
    /// other waits for the user, and the session needs them.
    /// </summary>
    private async Task OnToolPermissionAsync(string requestId, ClaudeCodeControlRequestBody request, ClaudeCodeProcessManager process)
    {
        // Fleet's own tools under their names, which every level allows (fleet_app_start under the shell's).
        var tool = ClaudeCodeTools.PermissionName(request.ToolName ?? "tool");
        var input = request.Input.ValueKind == System.Text.Json.JsonValueKind.Undefined ? default : request.Input.Clone();

        // A question is the user's to answer, at any permission level: its call shows as Fleet's question card.
        if (ClaudeCodeTools.IsQuestion(tool) && request.ToolUseId is { } questionCall)
        {
            _questions[questionCall] = new PendingQuestion(requestId, input);
            await _eventChannel.Writer.WriteAsync(PermissionEvents.Status(ActivityStatuses.WaitingInput, _fleetSessionId), CancellationToken.None)
                .ConfigureAwait(false);
            return;
        }

        if (_permissions.Decide(tool, ClaudeCodePermissions.Patterns(tool, input)) is { } decision)
        {
            await process.WriteLineAsync(
                decision == PermissionReplies.Reject
                    ? ClaudeCodeInput.Deny(requestId, PermissionPolicy.UnattendedRejection)
                    : ClaudeCodeInput.Allow(requestId, input),
                CancellationToken.None).ConfigureAwait(false);
            return;
        }

        var ask = ClaudeCodePermissions.ToAsk(requestId, _fleetSessionId, request, _workingDirectory);
        _askInputs[requestId] = input;
        if (!_permissions.Pending.Add(ask, _fleetSessionId))
            return;

        await _eventChannel.Writer.WriteAsync(PermissionEvents.Asked(ask, _fleetSessionId), CancellationToken.None).ConfigureAwait(false);
        await _eventChannel.Writer.WriteAsync(PermissionEvents.Status(ActivityStatuses.WaitingInput, _fleetSessionId), CancellationToken.None)
            .ConfigureAwait(false);
    }

    /// <summary>The ask no longer waits; once none does, the turn is working again.</summary>
    private void SettlePermission(string requestId, string reply)
    {
        _askInputs.TryRemove(requestId, out _);
        if (_permissions.Pending.Remove(requestId) is not { } settled)
            return;
        _eventChannel.Writer.TryWrite(PermissionEvents.Replied(settled.Ask, reply, _fleetSessionId));
        if (!WaitsOnUser && reply != PermissionReplies.Gone)
            _eventChannel.Writer.TryWrite(PermissionEvents.Status(ActivityStatuses.Busy, _fleetSessionId));
    }

    /// <summary>The asks still waiting when the turn or its process ended: nothing will answer them now.</summary>
    private void ForgetPermissionAsks()
    {
        _askInputs.Clear();
        _questions.Clear();
        foreach (var entry in _permissions.Pending.Clear())
            _eventChannel.Writer.TryWrite(PermissionEvents.Replied(entry.Ask, PermissionReplies.Gone, _fleetSessionId));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Answers an <c>AskUserQuestion</c> call. <paramref name="requestId"/> is the tool call's id (what the question card
    /// knows) or Claude Code's request's. The call runs with its questions and the answers, by question text, the
    /// chosen labels joined with ", ".
    /// </remarks>
    public async Task AnswerQuestionAsync(string requestId, IReadOnlyList<IReadOnlyList<string>> answers, CancellationToken ct)
    {
        var (callId, question) = FindQuestion(requestId);
        var byQuestion = new Dictionary<string, string>(StringComparer.Ordinal);
        if (question.Input.ValueKind == System.Text.Json.JsonValueKind.Object
            && question.Input.TryGetProperty("questions", out var questions)
            && questions.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            var index = 0;
            foreach (var asked in questions.EnumerateArray())
            {
                if (PermissionEvents.String(asked, "question") is { } text)
                    byQuestion[text] = index < answers.Count ? string.Join(", ", answers[index]) : string.Empty;
                index++;
            }

            _answers[callId] = answers;
            await AnswerAsync(callId, question, ClaudeCodeInput.Answer(question.RequestId, questions, byQuestion), ct).ConfigureAwait(false);
            return;
        }

        await AnswerAsync(callId, question, ClaudeCodeInput.Deny(question.RequestId, "The question couldn't be read, so it wasn't asked."), ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>The call is refused, and the agent told the user dismissed it.</remarks>
    public Task RejectQuestionAsync(string requestId, CancellationToken ct)
    {
        var (callId, question) = FindQuestion(requestId);
        return AnswerAsync(callId, question, ClaudeCodeInput.Deny(question.RequestId, "The user dismissed the question without answering."), ct);
    }

    private (string CallId, PendingQuestion Question) FindQuestion(string requestId)
    {
        if (_questions.TryGetValue(requestId, out var byCall))
            return (requestId, byCall);
        foreach (var (callId, question) in _questions)
        {
            if (question.RequestId == requestId)
                return (callId, question);
        }

        throw new KeyNotFoundException($"Question '{requestId}' isn't waiting for an answer.");
    }

    private async Task AnswerAsync(string callId, PendingQuestion question, string line, CancellationToken ct)
    {
        if (_process is not { } process || !await process.WriteLineAsync(line, ct).ConfigureAwait(false))
        {
            _questions.TryRemove(callId, out _);
            throw new KeyNotFoundException($"Question '{callId}' isn't waiting for an answer: Claude Code has stopped.");
        }

        if (_questions.TryRemove(new KeyValuePair<string, PendingQuestion>(callId, question)) && !WaitsOnUser)
            _eventChannel.Writer.TryWrite(PermissionEvents.Status(ActivityStatuses.Busy, _fleetSessionId));
    }

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(CancellationToken ct)
    {
        if (_process?.IsRunning == true)
        {
            return Task.FromResult(new HealthCheckResult(true, _turn is null ? "Claude Code is running." : "Prompt active."));
        }

        return _status is HarnessSessionStatus.Idle or HarnessSessionStatus.Stopping
            ? Task.FromResult(new HealthCheckResult(true, null))
            : Task.FromResult(new HealthCheckResult(false, $"Unexpected status: {_status}"));
    }

    /// <inheritdoc />
    public Task<string?> AskOffTheRecordAsync(string prompt, CancellationToken ct)
        => Task.FromResult<string?>(null);

    /// <inheritdoc />
    public Task<string?> GetActivityStatusAsync(CancellationToken ct)
    {
        // ClaudeCode doesn't have a status query endpoint, so we return null
        return Task.FromResult<string?>(null);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Claude Code's <c>stop_task</c> request stops one task and leaves the turn and the other tasks running. Its end comes
    /// back as a <c>task_notification</c> with status <c>stopped</c>.
    /// </remarks>
    public async Task<bool> StopWorkAsync(string workId, CancellationToken ct)
    {
        ClaudeCodeProcessManager? process;
        lock (_gate)
            process = _tasks.IsRunning(workId) ? _process : null;
        if (process is not { IsRunning: true })
            return false;

        var requestId = $"fleet-{Guid.NewGuid():N}";
        var line = ClaudeCodeInput.StopTask(requestId, workId);
        var answer = await process.RequestAsync(requestId, line, _shutdownTimeout, ct).ConfigureAwait(false);
        if (answer?.Subtype == "success")
            return true;

        LogRequestRefused(_logger, _fleetSessionId, line, answer?.Error ?? "no answer", null);
        lock (_gate)
        {
            // It ended meanwhile.
            if (!_tasks.IsRunning(workId))
                return false;
        }

        throw new NotSupportedException($"Claude Code didn't stop it: {answer?.Error ?? "it didn't answer"}.");
    }

    /// <inheritdoc />
    /// <remarks>
    /// Tails the file Claude Code writes a background command's or monitor's output to. The output of work that ended stays
    /// readable for as long as Claude Code keeps the file. Until Fleet has seen where that is (a monitor started before any
    /// background command says), Claude Code's <c>get_task_output</c> gives the last 8 KiB.
    /// </remarks>
    public async Task<WorkOutput?> ReadWorkOutputAsync(string workId, long offset, CancellationToken ct)
    {
        string? file;
        ClaudeCodeProcessManager? process;
        lock (_gate)
        {
            file = _tasks.OutputFile(workId);
            process = _tasks.IsRunning(workId) ? _process : null;
        }

        if (file is not null)
            return ClaudeCodeTaskOutput.Read(file, offset);
        if (process is not { IsRunning: true })
            return null;

        var requestId = $"fleet-{Guid.NewGuid():N}";
        var answer = await process.RequestAsync(requestId, ClaudeCodeInput.GetTaskOutput(requestId, workId), _shutdownTimeout, ct)
            .ConfigureAwait(false);
        return answer is { Subtype: "success", Response: { } tail } ? ClaudeCodeTaskOutput.FromTail(tail, offset) : null;
    }

    /// <inheritdoc />
    /// <remarks>
    /// What the session's claude process still runs; nothing when there's no process, since its work ends with it. A
    /// subagent's child session can't say: its work runs in its parent's process.
    /// </remarks>
    public Task<IReadOnlyList<WorkReport>?> GetRunningWorkAsync(CancellationToken ct)
    {
        if (_readOnlyChild)
            return Task.FromResult<IReadOnlyList<WorkReport>?>(null);
        lock (_gate)
            return Task.FromResult<IReadOnlyList<WorkReport>?>(_process is null ? [] : _tasks.Running());
    }

    /// <inheritdoc />
    public Task WaitForEventSubscriptionAsync(CancellationToken ct)
    {
        // ClaudeCode uses stdio streams which are synchronously available when the process starts.
        // No async subscription establishment is needed.
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AgentInfo>> GetAgentsAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<AgentInfo>>([]);

    /// <inheritdoc />
    public Task<IReadOnlyList<CommandInfo>> GetCommandsAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<CommandInfo>>([]);

    /// <inheritdoc />
    /// <remarks>Claude Code's own list, with each model's efforts (<see cref="ClaudeCodeCatalog"/>).</remarks>
    public async Task<IReadOnlyList<ProviderInfo>> GetProvidersAsync(CancellationToken ct)
        => _catalog is null ? [] : await _catalog.GetProvidersAsync(_workingDirectory, ct).ConfigureAwait(false);

    /// <inheritdoc />
    /// <remarks>Ends the claude process, and with it any work the agent left running in the background.</remarks>
    public async Task StopAsync(CancellationToken ct)
    {
        LogStop(_logger, InstanceId, null);

        ClaudeCodeProcessManager? process;
        lock (_gate)
        {
            _status = HarnessSessionStatus.Stopping;
            DisarmIdleTimer();
            process = _process;
        }

        if (process is not null)
        {
            await StopProcessAsync(process, "the session stopped").ConfigureAwait(false);
        }

        _eventChannel.Writer.TryComplete();
        _status = HarnessSessionStatus.Stopped;
    }

    /// <inheritdoc />
    public Task DeleteAsync(CancellationToken ct) => StopAsync(ct);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_status is not HarnessSessionStatus.Stopped and not HarnessSessionStatus.Error)
        {
            try
            {
                await StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best effort on dispose
            }
        }

        if (_process is { } process)
        {
            await process.DisposeAsync().ConfigureAwait(false);
        }

        lock (_gate)
            DisarmIdleTimer();
    }

    // -----------------------------------------------------------------------
    // Turns, background work and the idle shutdown
    // -----------------------------------------------------------------------

    /// <summary>Starts a turn: Fleet's prompt (<paramref name="process"/> set once it's sent) or one Claude Code started.</summary>
    private Turn BeginTurn(ClaudeCodeProcessManager? process)
    {
        var turn = new Turn { Process = process };
        _turn = turn;
        _aborting = false;
        _root.Clear();
        _status = HarnessSessionStatus.Running;
        DisarmIdleTimer();

        if (_config.ProcessTimeoutSeconds is > 0 and var seconds)
        {
            turn.Timeout = Time.CreateTimer(
                _ => _ = TimeOutAsync(turn), null, TimeSpan.FromSeconds(seconds), Timeout.InfiniteTimeSpan);
        }

        return turn;
    }

    /// <summary>Ends <paramref name="turn"/> if it's still the running one; true when it was.</summary>
    private bool EndTurn(Turn turn)
    {
        if (!ReferenceEquals(_turn, turn))
            return false;

        _turn = null;
        turn.Timeout?.Dispose();
        if (_status is not HarnessSessionStatus.Stopping and not HarnessSessionStatus.Stopped)
            _status = HarnessSessionStatus.Idle;
        ArmIdleTimer();
        turn.Done.TrySetResult();
        return true;
    }

    /// <summary>A turn that ran past <see cref="ClaudeCodeOptions.ProcessTimeoutSeconds"/>: its process is killed.</summary>
    private async Task TimeOutAsync(Turn turn)
    {
        ClaudeCodeProcessManager? process;
        lock (_gate)
            process = ReferenceEquals(_turn, turn) ? turn.Process : null;

        if (process is null)
            return;

        process.StopReason ??= $"a turn ran past the {_config.ProcessTimeoutSeconds}-second limit";
        await process.StopForTimeoutAsync(_shutdownTimeout).ConfigureAwait(false);
    }

    /// <summary>The process this line came from, and the turn it belongs to; a line with no turn starts one.</summary>
    private void JoinTurn(ClaudeCodeProcessManager processManager)
    {
        bool woke;
        lock (_gate)
        {
            if (_status is HarnessSessionStatus.Stopping or HarnessSessionStatus.Stopped)
                return;

            woke = _turn is null;
            if (woke)
                BeginTurn(processManager);
            else
                _turn!.Process ??= processManager;
        }

        if (woke)
        {
            // Claude Code went on by itself, e.g. when background work it was waiting on finished.
            LogWakeTurn(_logger, _fleetSessionId, null);
            _eventChannel.Writer.TryWrite(ClaudeCodeMapper.CreateSessionStatusEvent(_fleetSessionId, "busy"));
        }
    }

    /// <summary>
    /// Keeps track of the work the agent left running, from Claude Code's <c>task_*</c> system messages, and says what
    /// changed in Fleet's running work.
    /// </summary>
    private IReadOnlyList<ClaudeCodeTasks.Change> TrackBackgroundWork(ClaudeCodeSystemMessage system)
    {
        IReadOnlyList<ClaudeCodeTasks.Change> changes;
        string running;
        lock (_gate)
        {
            var before = _tasks.Background.Count;
            changes = _tasks.Observe(system);
            if (changes.Count == 0 && before == _tasks.Background.Count && system.Subtype != "background_tasks_changed")
                return changes;

            if (_tasks.HasBackgroundWork)
                DisarmIdleTimer();
            else if (_turn is null)
                ArmIdleTimer();
            running = DescribeBackgroundWork();
        }

        LogBackgroundWork(_logger, _fleetSessionId, $"{system.Subtype} {system.TaskId}".TrimEnd(), running, null);
        return changes;
    }

    private string DescribeBackgroundWork()
        => !_tasks.HasBackgroundWork
            ? "nothing"
            : string.Join(", ", _tasks.Background.Select(work => $"{work.Key} ({work.Value})"));

    /// <summary>
    /// Starts the idle countdown: with no turn running and no background work, the process stops after
    /// <see cref="ClaudeCodeOptions.IdleShutdownSeconds"/>, so an idle session doesn't keep a claude process for ever.
    /// </summary>
    private void ArmIdleTimer()
    {
        DisarmIdleTimer();
        if (_config.IdleShutdownSeconds is not > 0 || _process is null || _turn is not null || _tasks.HasBackgroundWork
            || _status is HarnessSessionStatus.Stopping or HarnessSessionStatus.Stopped)
        {
            return;
        }

        var process = _process;
        _idleTimer = Time.CreateTimer(
            _ => _ = StopIfIdleAsync(process), null, TimeSpan.FromSeconds(_config.IdleShutdownSeconds), Timeout.InfiniteTimeSpan);
    }

    private void DisarmIdleTimer()
    {
        _idleTimer?.Dispose();
        _idleTimer = null;
    }

    private async Task StopIfIdleAsync(ClaudeCodeProcessManager process)
    {
        // A prompt on its way in: it's not idle.
        if (!await _promptLock.WaitAsync(0).ConfigureAwait(false))
            return;

        try
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_process, process) || _turn is not null || _tasks.HasBackgroundWork)
                    return;
            }

            await StopProcessAsync(process, $"idle for {_config.IdleShutdownSeconds} seconds with no background work")
                .ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // The session was disposed meanwhile.
        }
        finally
        {
            try
            {
                _promptLock.Release();
            }
            catch (ObjectDisposedException)
            {
                // Disposed meanwhile.
            }
        }
    }

    /// <summary>
    /// The process ended: it's no longer the session's, and the work it was running is gone with it. Returns that work,
    /// ended <see cref="WorkEndedReasons.Lost"/>, for Fleet to be told. Called with <see cref="_gate"/> held.
    /// </summary>
    private IReadOnlyList<ClaudeCodeTasks.Change> ForgetProcess()
    {
        _process = null;
        _processSettings = null;
        DisarmIdleTimer();
        return _tasks.EndAll();
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    internal async Task PumpStdoutAsync(StreamReader stdout, ClaudeCodeProcessManager processManager)
    {
        // This process's subagents' conversations, by the call that started each; null where no child session could be made.
        var children = new Dictionary<string, Conversation?>(StringComparer.Ordinal);
        try
        {
            await foreach (var msg in ClaudeCodeStdioClient
                .ReadMessagesAsync(stdout, _logger, CancellationToken.None)
                .ConfigureAwait(false))
            {
                // Capture session ID from init message. Every turn opens with one, including one Claude Code starts
                // by itself.
                if (msg is ClaudeCodeSystemMessage { Subtype: "init" } init)
                {
                    JoinTurn(processManager);

                    if (init.SessionId is not null && init.SessionId != _claudeSessionId)
                    {
                        _claudeSessionId = init.SessionId;
                        LogSessionId(_logger, init.SessionId, null);
                        // Persist resume token for session recovery
                        _ = PersistResumeTokenAsync(init.SessionId);
                    }

                    if (init.Model is not null)
                        _modelId = init.Model;
                }
                else if (msg is ClaudeCodeSystemMessage system)
                {
                    if (ClaudeCodeMapper.TryMapCompaction(system, _fleetSessionId) is { } compaction)
                        await _eventChannel.Writer.WriteAsync(compaction, CancellationToken.None).ConfigureAwait(false);
                    else
                        await ReportWorkAsync(TrackBackgroundWork(system), children).ConfigureAwait(false);
                }
                else if (msg is ClaudeCodeStreamEvent { Event: { } streamed } streamEvent)
                {
                    // The text as the model writes it: a subagent's in its child session, the session's own in its turn.
                    Conversation? conversation;
                    if (streamEvent.ParentToolUseId is { } subagentCall)
                    {
                        conversation = await ChildAsync(subagentCall, children).ConfigureAwait(false);
                    }
                    else
                    {
                        JoinTurn(processManager);
                        conversation = _root;
                    }

                    if (conversation is not null)
                        StreamPart(conversation, streamed);
                }
                else if (msg is ClaudeCodeAssistantMessage assistantMsg)
                {
                    ObserveToolCalls(assistantMsg);

                    // Lines with a parent_tool_use_id are a subagent's own steps, which go to its child session. They
                    // don't belong to a turn of the session's: a background subagent works on after it.
                    if (assistantMsg.ParentToolUseId is { } subagentCall)
                    {
                        if (await ChildAsync(subagentCall, children).ConfigureAwait(false) is { } child)
                            await AddAssistantBlocksAsync(child, assistantMsg).ConfigureAwait(false);
                        continue;
                    }

                    JoinTurn(processManager);
                    await AddAssistantBlocksAsync(_root, assistantMsg).ConfigureAwait(false);

                    if (assistantMsg.Message?.Model is not null)
                        _modelId = assistantMsg.Message.Model;

                    if (assistantMsg.Message is { Id: { } callId, Usage: { } usage } && callId != _reportedContextCallId)
                    {
                        _reportedContextCallId = callId;
                        await _eventChannel.Writer.WriteAsync(ContextUsageEvent(ClaudeCodeMapper.ToContextCall(usage)), CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                }
                else if (msg is ClaudeCodeUserMessage userMsg)
                {
                    ObserveToolResults(userMsg);
                    if (userMsg.ParentToolUseId is { } subagentCall)
                    {
                        if (await ChildAsync(subagentCall, children).ConfigureAwait(false) is { } child)
                            await AddUserLineAsync(child, userMsg).ConfigureAwait(false);
                    }
                    else
                    {
                        await ApplyToolResultsAsync(_root, userMsg).ConfigureAwait(false);
                    }
                }
                else if (msg is ClaudeCodeControlResponse { Response: { } response })
                {
                    processManager.CompleteRequest(response);
                }
                else if (msg is ClaudeCodeControlRequest { RequestId: { } requestId, Request: { Subtype: "can_use_tool" } toolRequest })
                {
                    await OnToolPermissionAsync(requestId, toolRequest, processManager).ConfigureAwait(false);
                }
                else if (msg is ClaudeCodeControlRequest { RequestId: { } unknownRequestId, Request: var unknownRequest })
                {
                    await processManager.WriteLineAsync(
                        ClaudeCodeInput.Error(unknownRequestId, $"Fleet doesn't answer '{unknownRequest?.Subtype}' requests."),
                        CancellationToken.None).ConfigureAwait(false);
                }
                else if (msg is ClaudeCodeControlCancelRequest { RequestId: { } cancelledId })
                {
                    SettlePermission(cancelledId, PermissionReplies.Gone);
                    foreach (var (callId, question) in _questions)
                    {
                        if (question.RequestId == cancelledId)
                            _questions.TryRemove(callId, out _);
                    }
                }
                else if (msg is ClaudeCodeResultMessage result)
                {
                    await EndTurnAsync(result, processManager).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPumpFailed(_logger, InstanceId, ex);
        }
        finally
        {
            await OnProcessEndedAsync(processManager, children).ConfigureAwait(false);
        }
    }

    /// <summary>Remembers the tool calls a line makes: which start background work, and in whose conversation.</summary>
    private void ObserveToolCalls(ClaudeCodeAssistantMessage assistantMsg)
    {
        lock (_gate)
        {
            foreach (var block in assistantMsg.Message?.Content ?? [])
            {
                if (block is ClaudeCodeToolUseBlock { Id: { } callId } toolUse)
                {
                    _tasks.ObserveToolUse(callId, toolUse.Name, toolUse.Input, assistantMsg.ParentToolUseId);
                    // Its call to Fleet's MCP server comes next, naming only this id; this says whether a subagent made it.
                    if (ClaudeCodeTools.FleetTool(toolUse.Name) is not null)
                        _bridgeTokens?.NoteCall(callId, assistantMsg.ParentToolUseId);
                }
            }
        }
    }

    /// <summary>Learns from tool results where background commands write their output.</summary>
    private void ObserveToolResults(ClaudeCodeUserMessage userMsg)
    {
        lock (_gate)
        {
            foreach (var block in userMsg.Message?.Content ?? [])
            {
                if (block is ClaudeCodeToolResultBlock result)
                    _tasks.ObserveToolResult(result.Content);
            }
        }
    }

    /// <summary>
    /// Tells Fleet what changed in the running work, each on the session whose conversation started it: a nested
    /// subagent's on the subagent's child session. A subagent's child session is made first, so its steps have
    /// somewhere to go, and it reads as working until its task ends.
    /// </summary>
    /// <param name="children">The process's subagent conversations; null once it has ended, when only the session's own is left.</param>
    private async Task ReportWorkAsync(IReadOnlyList<ClaudeCodeTasks.Change> changes, Dictionary<string, Conversation?>? children)
    {
        foreach (var change in changes)
        {
            var owner = change.OwnerCallId is { } ownerCall && children is not null
                ? (await ChildAsync(ownerCall, children).ConfigureAwait(false))?.FleetSessionId ?? _fleetSessionId
                : _fleetSessionId;

            Conversation? subagent = null;
            if (change.Report.ChildHarnessSessionId is { } subagentCall && children is not null)
                subagent = await ChildAsync(subagentCall, children).ConfigureAwait(false);
            else if (change.Type == EventTypes.WorkEnded && children is not null)
                subagent = children.Values.FirstOrDefault(c => c?.TaskId == change.Report.WorkId);

            if (change.Type == EventTypes.WorkStarted && subagent is not null)
            {
                subagent.TaskId = change.Report.WorkId;
                subagent.Busy = true;
                if (change.Prompt is { Length: > 0 } prompt)
                    await AddPromptAsync(subagent, prompt).ConfigureAwait(false);
                _eventChannel.Writer.TryWrite(ForSession(ClaudeCodeMapper.CreateSessionStatusEvent(subagent.FleetSessionId, "busy"), subagent.FleetSessionId));
            }

            var fleetSessionId = owner == _fleetSessionId ? null : owner;
            _eventChannel.Writer.TryWrite(change.Type switch
            {
                EventTypes.WorkStarted => WorkEvents.Started(change.Report, _fleetSessionId, fleetSessionId),
                EventTypes.WorkEnded => WorkEvents.Ended(change.Report, change.Report.EndedReason ?? WorkEndedReasons.Completed, _fleetSessionId, fleetSessionId),
                _ => WorkEvents.Updated(change.Report, _fleetSessionId, fleetSessionId),
            });

            if (change.Type == EventTypes.WorkEnded && subagent is { Busy: true })
            {
                await FailUnfinishedToolsAsync(subagent).ConfigureAwait(false);
                subagent.Busy = false;
                _eventChannel.Writer.TryWrite(ForSession(ClaudeCodeMapper.CreateSessionIdleEvent(subagent.FleetSessionId), subagent.FleetSessionId));
            }
        }
    }

    private static HarnessEvent ForSession(HarnessEvent evt, string fleetSessionId) => evt with { FleetSessionId = fleetSessionId };

    /// <summary>
    /// The conversation of the subagent called with <paramref name="callId"/>, in a hidden child session under the
    /// conversation that made the call: the session's own, or (nested) another subagent's. Made when first needed; null
    /// when it can't be.
    /// </summary>
    private async Task<Conversation?> ChildAsync(string callId, Dictionary<string, Conversation?> children)
    {
        if (children.TryGetValue(callId, out var known))
            return known;

        ClaudeCodeTasks.Call? call;
        lock (_gate)
            call = _tasks.FindCall(callId);

        // Taken before the parent is looked up, so a call can't lead back to itself.
        children[callId] = null;
        var parent = call?.ParentCallId is { } parentCall ? await ChildAsync(parentCall, children).ConfigureAwait(false) : _root;
        string? childSessionId = null;
        if (parent is not null)
        {
            try
            {
                childSessionId = await ChildSessions(parent.FleetSessionId, callId, call?.Description ?? "subagent").ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogChildFailed(_logger, _fleetSessionId, callId, ex);
            }
        }

        if (childSessionId is null)
        {
            LogChildFailed(_logger, _fleetSessionId, callId, null);
            return null;
        }

        var child = new Conversation(childSessionId);
        children[callId] = child;
        if (parent is not null)
            await LinkChildAsync(parent, callId, childSessionId).ConfigureAwait(false);
        return child;
    }

    /// <summary>
    /// The subagent call names the session its steps went to (<c>metadata.sessionId</c>), as OpenCode's do, so its row
    /// opens it wherever the conversation is read, after a reload too.
    /// </summary>
    private async Task LinkChildAsync(Conversation parent, string callId, string childSessionId)
    {
        if (!parent.ToolCallMessageIds.TryGetValue(callId, out var claudeMessageId)
            || !parent.Messages.TryGetValue(claudeMessageId, out var message))
        {
            return;
        }

        var parts = message.Parts.ToList();
        var index = parts.FindIndex(part => part is ToolUsePart tool && tool.ToolCallId == callId);
        if (index < 0)
            return;

        var linked = (ToolUsePart)parts[index] with { Metadata = ClaudeCodeTools.ChildSession(childSessionId) };
        parts[index] = linked;
        message = message with { Parts = parts };
        parent.Messages[claudeMessageId] = message;
        await PersistMessageAsync(parent.FleetSessionId, message, [ClaudeCodeMapper.CreatePartUpdatedEvent(message.Id, parent.FleetSessionId, linked, index)])
            .ConfigureAwait(false);
    }

    /// <summary>Fleet's hidden child session for a subagent, made like any delegated child (<see cref="SessionOrchestrator"/>).</summary>
    private async Task<string?> MakeChildSessionAsync(string parentFleetSessionId, string callId, string title)
    {
        using var userScope = BackgroundUserContext.BeginScope(_ownerUserId);
        using var scope = _scopeFactory.CreateScope();
        if (scope.ServiceProvider.GetService<SessionOrchestrator>() is not { } orchestrator)
            return null;

        var child = await orchestrator.EnsureDelegatedChildSessionAsync(parentFleetSessionId, callId, title).ConfigureAwait(false);
        return child.IsSuccess ? child.Value.Id : null;
    }

    /// <summary>A model call's size (or, with none, the model's limits), with the limits the last result line gave.</summary>
    private HarnessEvent ContextUsageEvent(ContextCall? call) => ContextEvents.Usage(
        new ContextUsageReport
        {
            Call = call,
            Limit = _contextWindow,
            CompactsAt = _contextWindow is { } window ? ClaudeCodeMapper.CompactsAt(window, _maxOutputTokens) : null,
            ModelId = _modelId,
            ProviderId = "anthropic",
        },
        _fleetSessionId);

    /// <summary>A result line: the turn is over. The process carries on, waiting for the next prompt.</summary>
    private async Task EndTurnAsync(ClaudeCodeResultMessage result, ClaudeCodeProcessManager processManager)
    {
        Turn? turn;
        lock (_gate)
            turn = _turn is { } running && (running.Process is null || ReferenceEquals(running.Process, processManager)) ? running : null;

        ForgetPermissionAsks();
        await SettleStreamingAsync(_root).ConfigureAwait(false);

        // Extract analytics
        if (_analyticsCollector is not null)
        {
            var tokenEvent = ClaudeCodeMapper.TryExtractTokenEvent(
                result,
                _fleetSessionId,
                _projectId,
                _projectName,
                _workingDirectory,
                _modelId,
                _ownerUserId);
            if (tokenEvent is not null)
                _analyticsCollector.AcceptTokenEvent(tokenEvent);
        }

        // The model's limits, and the last call's real output count, come with the result.
        var limits = ClaudeCodeMapper.ReadModelLimits(result, _modelId);
        var limitsChanged = limits is { } known && (known.ContextWindow != _contextWindow || known.MaxOutputTokens != _maxOutputTokens);
        if (limits is { } newLimits)
            (_contextWindow, _maxOutputTokens) = newLimits;
        var lastCall = ClaudeCodeMapper.LastCall(result);
        if (lastCall is not null || limitsChanged)
            await _eventChannel.Writer.WriteAsync(ContextUsageEvent(lastCall), CancellationToken.None).ConfigureAwait(false);

        if (result.SessionId is not null && _claudeSessionId is null)
        {
            _claudeSessionId = result.SessionId;
            // Persist resume token captured from result message (fallback)
            _ = PersistResumeTokenAsync(result.SessionId);
        }

        if (_aborting)
        {
            // An interrupted turn ends in an error result; the user asked for it, so there's nothing to say, but its
            // tool calls won't finish now.
            await FailUnfinishedToolsAsync(_root).ConfigureAwait(false);
        }
        else if (ClaudeCodeMapper.DescribeFailedResult(result) is { } failure && !TurnEndsWithText(failure))
        {
            // Claude Code writes some failures as an assistant message already, e.g. "Not logged in".
            await PersistNoticeAsync($"Claude Code stopped: {failure}").ConfigureAwait(false);
        }

        if (turn is null)
            return;

        lock (_gate)
        {
            if (!EndTurn(turn))
                return;
        }

        foreach (var evt in ClaudeCodeMapper.ToFrontendEvents(result, _fleetSessionId))
            await _eventChannel.Writer.WriteAsync(evt, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// The process's output ended: it exited, or was stopped. A turn still running on it ends here; unless the user
    /// stopped it, the conversation says why, since otherwise the prompt just goes unanswered. Work it still ran is lost,
    /// and Fleet is told so.
    /// </summary>
    private async Task OnProcessEndedAsync(ClaudeCodeProcessManager processManager, Dictionary<string, Conversation?> children)
    {
        Turn? turn;
        bool current;
        string lostWork = string.Empty;
        int lostCount;
        IReadOnlyList<ClaudeCodeTasks.Change> lost = [];
        lock (_gate)
        {
            turn = _turn is { } running && ReferenceEquals(running.Process, processManager) ? running : null;
            current = _process is null || ReferenceEquals(_process, processManager);
            lostCount = current ? _tasks.Background.Count : 0;
            if (lostCount > 0)
                lostWork = DescribeBackgroundWork();
            if (current && _process is not null)
                lost = ForgetProcess();
            else if (current)
                lost = _tasks.EndAll();
        }

        var stopping = _status is HarnessSessionStatus.Stopping or HarnessSessionStatus.Stopped;
        var reason = processManager.StopReason ?? "it exited";
        LogProcessEnded(_logger, _fleetSessionId, reason, null);
        if (lostCount > 0)
            LogWorkLost(_logger, _fleetSessionId, reason, lostCount, lostWork, null);

        await ReportWorkAsync(lost, children).ConfigureAwait(false);

        // A subagent cut off with its process: its calls won't finish, and it isn't working any more.
        foreach (var child in children.Values.OfType<Conversation>())
        {
            await SettleStreamingAsync(child).ConfigureAwait(false);
            await FailUnfinishedToolsAsync(child).ConfigureAwait(false);
            if (child.Busy)
            {
                child.Busy = false;
                _eventChannel.Writer.TryWrite(ForSession(ClaudeCodeMapper.CreateSessionIdleEvent(child.FleetSessionId), child.FleetSessionId));
            }
        }

        if (turn is not null)
        {
            await SettleStreamingAsync(_root).ConfigureAwait(false);
            await FailUnfinishedToolsAsync(_root).ConfigureAwait(false);

            if (!_aborting && !stopping)
            {
                var exitCode = await processManager.WaitForExitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                await PersistNoticeAsync(DescribeEarlyExit(processManager, exitCode)).ConfigureAwait(false);
            }
        }

        if (current)
            ForgetPermissionAsks();
        RevokeBridgeToken(processManager);
        await processManager.DisposeAsync().ConfigureAwait(false);

        if (turn is null)
            return;

        bool ended;
        lock (_gate)
            ended = EndTurn(turn);

        // Without a result line nothing has said the turn is over.
        if (ended && !stopping)
            _eventChannel.Writer.TryWrite(ClaudeCodeMapper.CreateSessionIdleEvent(_fleetSessionId));
    }

    /// <summary>The process ended: its calls to Fleet's API stop working.</summary>
    private void RevokeBridgeToken(ClaudeCodeProcessManager processManager)
    {
        if (processManager.BridgeToken is { } token)
            _bridgeTokens?.Revoke(token);
    }

    /// <summary>
    /// A streaming event (<c>--include-partial-messages</c>): the model's text and thinking go out as part deltas while
    /// it writes them, under the message and part ids the finished blocks get, so the finished <c>assistant</c> line
    /// replaces what the deltas built up. Deltas aren't saved; the finished block is.
    /// </summary>
    private void StreamPart(Conversation conversation, ClaudeCodeStreamEventBody streamed)
    {
        switch (streamed.Type)
        {
            case "message_start" when streamed.Message?.Id is { } claudeId:
                // Fleet's id for the message is given now, so the deltas name the message the finished blocks go to.
                if (!conversation.Messages.ContainsKey(claudeId))
                {
                    conversation.Messages[claudeId] = new HarnessMessage
                    {
                        Id = AscendingMessageId.New(),
                        Role = "assistant",
                        Parts = [],
                        Timestamp = DateTimeOffset.UtcNow,
                        ModelId = streamed.Message.Model,
                    };
                }

                conversation.StreamingMessageId = claudeId;
                conversation.Streaming = null;
                break;

            case "content_block_start" when conversation.StreamingMessageId is { } claudeId
                                            && conversation.Messages.TryGetValue(claudeId, out var message):
                // The part the finished block will be: the message's next.
                var partId = $"{message.Id}-part-{message.Parts.Count}";
                conversation.Streaming = streamed.ContentBlock switch
                {
                    ClaudeCodeTextBlock => new StreamingBlock(claudeId, "text", partId),
                    ClaudeCodeThinkingBlock => new StreamingBlock(claudeId, "reasoning", partId),
                    _ => null,
                };
                break;

            case "content_block_delta" when conversation.Streaming is { } block
                                            && Delta(streamed.Delta, block.Kind) is { Length: > 0 } delta
                                            && conversation.Messages.TryGetValue(block.ClaudeMessageId, out var streaming):
                if (!block.Shown)
                {
                    block.Shown = true;
                    MessagePart empty = block.Kind == "text" ? new TextPart(string.Empty) { PartId = block.PartId } : new ReasoningPart(string.Empty) { PartId = block.PartId };
                    _eventChannel.Writer.TryWrite(ForSession(ClaudeCodeMapper.CreateMessageUpdatedEvent(streaming, conversation.FleetSessionId), conversation.FleetSessionId));
                    if (ClaudeCodeMapper.CreatePartUpdatedEvent(streaming.Id, conversation.FleetSessionId, empty, streaming.Parts.Count) is { } started)
                        _eventChannel.Writer.TryWrite(ForSession(started, conversation.FleetSessionId));
                }

                block.Text.Append(delta);
                _eventChannel.Writer.TryWrite(ForSession(
                    ClaudeCodeMapper.CreatePartDeltaEvent(streaming.Id, conversation.FleetSessionId, block.PartId, delta), conversation.FleetSessionId));
                break;

            case "message_stop":
                conversation.Streaming = null;
                break;
        }
    }

    /// <summary>What a delta adds to a block of <paramref name="kind"/>; null for anything else (a tool's input, a signature).</summary>
    private static string? Delta(ClaudeCodeStreamDelta? delta, string kind) => (delta?.Type, kind) switch
    {
        ("text_delta", "text") => delta.Text,
        ("thinking_delta", "reasoning") => delta.Thinking,
        _ => null,
    };

    /// <summary>
    /// A block whose first words went out but whose finished line never came (the turn was stopped, the process ended):
    /// it's saved as far as it got, so the conversation shows after a reload what it showed then.
    /// </summary>
    private async Task SettleStreamingAsync(Conversation conversation)
    {
        if (conversation.Streaming is not { Shown: true } block || !conversation.Messages.TryGetValue(block.ClaudeMessageId, out var message))
        {
            conversation.Streaming = null;
            return;
        }

        conversation.Streaming = null;
        var text = block.Text.ToString();
        MessagePart part = block.Kind == "text" ? new TextPart(text) { PartId = block.PartId } : new ReasoningPart(text) { PartId = block.PartId };
        var index = message.Parts.Count;
        message = message with { Parts = [.. message.Parts, part] };
        conversation.Messages[block.ClaudeMessageId] = message;
        var partEvent = ClaudeCodeMapper.CreatePartUpdatedEvent(message.Id, conversation.FleetSessionId, part, index);
        await PersistMessageAsync(conversation.FleetSessionId, message, [partEvent]).ConfigureAwait(false);
        if (partEvent is not null)
            _eventChannel.Writer.TryWrite(ForSession(partEvent, conversation.FleetSessionId));
    }

    /// <summary>Adds an assistant line's content blocks to its message in <paramref name="conversation"/> and saves it.</summary>
    private async Task AddAssistantBlocksAsync(Conversation conversation, ClaudeCodeAssistantMessage assistantMsg)
    {
        foreach (var block in assistantMsg.Message?.Content ?? [])
        {
            // Claude Code's own name and input, which say what a file tool changed once it returns.
            if (block is ClaudeCodeToolUseBlock { Id: { } callId } raw)
                conversation.ToolCalls[callId] = raw;
        }

        var incoming = ClaudeCodeMapper.ToHarnessMessage(assistantMsg, DateTimeOffset.UtcNow);
        if (incoming.Parts.Count == 0)
            return;

        var message = conversation.Messages.TryGetValue(incoming.Id, out var existing)
            ? existing
            : incoming with { Id = AscendingMessageId.New(), Parts = [] };

        var parts = message.Parts.ToList();
        var partEvents = new List<HarnessEvent?>(incoming.Parts.Count);
        foreach (var part in incoming.Parts)
        {
            // Part ids are fixed when a part is first seen, so a reloaded session and later live
            // updates (a tool's result) name the same part.
            var index = parts.Count;
            var partId = $"{message.Id}-part-{index}";
            MessagePart stamped = part switch
            {
                TextPart text => text with { PartId = partId },
                ReasoningPart reasoning => reasoning with { PartId = partId },
                ToolUsePart tool => tool with { PartId = partId },
                _ => part,
            };

            if (stamped is ToolUsePart toolUse)
                conversation.ToolCallMessageIds[toolUse.ToolCallId] = incoming.Id;

            parts.Add(stamped);
            partEvents.Add(ClaudeCodeMapper.CreatePartUpdatedEvent(message.Id, conversation.FleetSessionId, stamped, index));
        }

        message = message with { Parts = parts, ModelId = message.ModelId ?? incoming.ModelId };
        conversation.Messages[incoming.Id] = message;
        await PersistMessageAsync(conversation.FleetSessionId, message, partEvents).ConfigureAwait(false);

        // The block the deltas built up is finished. Its saved event goes out after the save; this one follows the
        // deltas on their own way, so a late delta can't land after it and double the text.
        if (conversation.Streaming is { Shown: true } streamed && streamed.ClaudeMessageId == incoming.Id)
        {
            conversation.Streaming = null;
            foreach (var partEvent in partEvents)
            {
                if (partEvent is not null && PartId(partEvent) == streamed.PartId)
                    _eventChannel.Writer.TryWrite(ForSession(partEvent, conversation.FleetSessionId));
            }
        }
    }

    private static string? PartId(HarnessEvent partEvent)
        => partEvent.Payload is { ValueKind: System.Text.Json.JsonValueKind.Object } payload
           && payload.TryGetProperty("part", out var part)
           && part.ValueKind == System.Text.Json.JsonValueKind.Object
            ? PermissionEvents.String(part, "id")
            : null;

    /// <summary>
    /// A user line in a subagent's conversation: the results of its tool calls, or what it was asked, which opens its
    /// child session like a prompt.
    /// </summary>
    private async Task AddUserLineAsync(Conversation conversation, ClaudeCodeUserMessage userMsg)
    {
        var blocks = userMsg.Message?.Content ?? [];
        if (blocks.Any(block => block is ClaudeCodeToolResultBlock))
        {
            await ApplyToolResultsAsync(conversation, userMsg).ConfigureAwait(false);
            return;
        }

        var text = string.Join("\n\n", blocks.OfType<ClaudeCodeTextBlock>().Select(block => block.Text).Where(t => !string.IsNullOrEmpty(t)));
        if (text.Length > 0)
            await AddPromptAsync(conversation, text).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens a subagent's child session with what it was asked: from its <c>task_started</c>, or (a foreground subagent)
    /// the user line its conversation starts with, which says the same once more.
    /// </summary>
    private async Task AddPromptAsync(Conversation conversation, string text)
    {
        if (string.Equals(conversation.Prompt, text, StringComparison.Ordinal))
            return;
        conversation.Prompt = text;

        var id = AscendingMessageId.New();
        var part = new TextPart(text) { PartId = $"{id}-part-0" };
        var message = new HarnessMessage { Id = id, Role = "user", Parts = [part], Timestamp = DateTimeOffset.UtcNow };
        await PersistMessageAsync(
            conversation.FleetSessionId,
            message,
            [ClaudeCodeMapper.CreatePartUpdatedEvent(id, conversation.FleetSessionId, part, 0)]).ConfigureAwait(false);
    }

    /// <summary>Marks the tool calls a user line answers as finished, with what they returned.</summary>
    private async Task ApplyToolResultsAsync(Conversation conversation, ClaudeCodeUserMessage userMsg)
    {
        foreach (var block in userMsg.Message?.Content ?? [])
        {
            if (block is not ClaudeCodeToolResultBlock { ToolUseId: { } callId } result
                || !conversation.ToolCallMessageIds.TryGetValue(callId, out var claudeMessageId)
                || !conversation.Messages.TryGetValue(claudeMessageId, out var message))
            {
                continue;
            }

            var parts = message.Parts.ToList();
            var index = parts.FindIndex(part => part is ToolUsePart tool && tool.ToolCallId == callId);
            if (index < 0)
                continue;

            var content = result.Content ?? string.Empty;
            var isError = result.IsError == true;
            var finished = (ToolUsePart)parts[index] with
            {
                State = isError ? ToolUseState.Error : ToolUseState.Completed,
                Error = isError ? content : null,
            };

            // What an edit changed, from the patch Claude Code reports; what the user answered a question with.
            if (!isError && conversation.ToolCalls.TryGetValue(callId, out var raw)
                && ClaudeCodeTools.Diff(raw.Name ?? string.Empty, raw.Input, userMsg.ToolUseResult) is { } diff)
            {
                finished = finished with { Metadata = ClaudeCodeTools.Metadata(diff) };
            }

            if (_answers.TryRemove(callId, out var answers))
                finished = finished with { Metadata = ClaudeCodeTools.Metadata(null, answers) };

            // One of Fleet's own tools: its card's title and what it made (a canvas, a screenshot), which Fleet's MCP
            // server kept for it, since the result Claude Code reports carries only what the model reads.
            if (conversation.ToolCalls.TryGetValue(callId, out var called) && ClaudeCodeTools.FleetTool(called.Name) is not null
                && FleetToolCalls()?.Take(callId) is { } record)
                finished = finished with { Title = record.Title, Metadata = record.Metadata };
            parts[index] = finished;
            parts.Add(new ToolResultPart(callId, content, isError));

            message = message with { Parts = parts };
            conversation.Messages[claudeMessageId] = message;
            await PersistMessageAsync(
                conversation.FleetSessionId,
                message,
                [ClaudeCodeMapper.CreatePartUpdatedEvent(message.Id, conversation.FleetSessionId, finished, index, isError ? null : content)])
                .ConfigureAwait(false);
        }
    }

    /// <summary>What Fleet's MCP server kept about its tool calls; none where Fleet doesn't run one (tests).</summary>
    private FleetToolCallRecords? FleetToolCalls()
    {
        if (_fleetToolCalls is null)
        {
            using var scope = _scopeFactory.CreateScope();
            _fleetToolCalls = scope.ServiceProvider.GetService<FleetToolCallRecords>();
        }

        return _fleetToolCalls;
    }

    /// <summary>Marks tool calls that never got a result as failed, so they don't show as running forever.</summary>
    private async Task FailUnfinishedToolsAsync(Conversation conversation)
    {
        foreach (var (claudeMessageId, message) in conversation.Messages.ToList())
        {
            var parts = message.Parts.ToList();
            var partEvents = new List<HarnessEvent?>();
            for (var i = 0; i < parts.Count; i++)
            {
                if (parts[i] is not ToolUsePart { State: ToolUseState.Pending or ToolUseState.Running } tool)
                    continue;

                var stopped = tool with { State = ToolUseState.Error, Error = "Stopped before it finished." };
                parts[i] = stopped;
                partEvents.Add(ClaudeCodeMapper.CreatePartUpdatedEvent(message.Id, conversation.FleetSessionId, stopped, i));
            }

            if (partEvents.Count == 0)
                continue;

            var updated = message with { Parts = parts };
            conversation.Messages[claudeMessageId] = updated;
            await PersistMessageAsync(conversation.FleetSessionId, updated, partEvents).ConfigureAwait(false);
        }
    }

    private bool TurnEndsWithText(string text)
        => _root.Messages.Values.LastOrDefault()?.Parts.OfType<TextPart>().LastOrDefault()?.Text.Trim() == text;

    private string DescribeEarlyExit(ClaudeCodeProcessManager processManager, int? exitCode)
    {
        if (processManager.TimedOut)
        {
            return $"Claude Code was stopped after {_config.ProcessTimeoutSeconds} seconds, "
                + "the limit set by Fleet:ClaudeCode:ProcessTimeoutSeconds.";
        }

        var text = exitCode is { } code
            ? $"Claude Code exited with code {code} before finishing."
            : "Claude Code stopped before finishing.";

        var stderr = processManager.StderrTail;
        return stderr.Count > 0
            ? $"{text}\n\n```\n{string.Join("\n", stderr.TakeLast(3))}\n```"
            : text;
    }

    /// <summary>Saves a note from Fleet, such as why a run failed, as an assistant message.</summary>
    private Task PersistNoticeAsync(string text)
    {
        var id = AscendingMessageId.New();
        var part = new TextPart(text) { PartId = $"{id}-part-0" };
        var message = new HarnessMessage
        {
            Id = id,
            Role = "assistant",
            Parts = [part],
            Timestamp = DateTimeOffset.UtcNow,
        };

        return PersistMessageAsync(_fleetSessionId, message, [ClaudeCodeMapper.CreatePartUpdatedEvent(id, _fleetSessionId, part, 0)]);
    }

    /// <summary>Saves the whole message in Fleet session <paramref name="sessionId"/> and publishes the parts that changed.</summary>
    private async Task PersistMessageAsync(string sessionId, HarnessMessage message, IReadOnlyList<HarnessEvent?> partEvents)
    {
        try
        {
            using var userScope = BackgroundUserContext.BeginScope(_ownerUserId);
            using var scope = _scopeFactory.CreateScope();
            var sessionActivityWriteService = scope.ServiceProvider.GetRequiredService<SessionActivityWriteService>();
            var persisted = MessagePersistenceService.ToPersistedMessage(sessionId, message);
            var outboxMessages = new List<OutboxMessage>();
            var createdAt = DateTimeOffset.UtcNow.ToString("O");

            var messageUpdatedEvent = ClaudeCodeMapper.CreateMessageUpdatedEvent(message, sessionId);
            outboxMessages.Add(CreateOutboxMessage(sessionId, messageUpdatedEvent, createdAt));

            foreach (var partEvent in partEvents)
            {
                if (partEvent is not null)
                    outboxMessages.Add(CreateOutboxMessage(sessionId, partEvent, createdAt));
            }

            await sessionActivityWriteService.WriteAsync(
                new SessionActivityWriteRequest
                {
                    MessagesToUpsert = [persisted],
                    OutboxMessages = outboxMessages
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Silent failure — persistence must never crash the instance
            LogPersistFailed(_logger, sessionId, ex);
        }
    }

    private async Task PersistUserPromptAsync(string text, PromptOptions? options, bool steered)
    {
        try
        {
            using var userScope = BackgroundUserContext.BeginScope(_ownerUserId);
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
            var message = MessagePersistenceService.CreateUserPromptMessage(
                text,
                DateTimeOffset.UtcNow,
                options?.Agent,
                options?.MessageId ?? AscendingMessageId.New(),
                options?.Attachments) with { Steered = steered };
            await repo.UpsertAsync(MessagePersistenceService.ToPersistedMessage(_fleetSessionId, message)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogPersistFailed(_logger, _fleetSessionId, ex);
        }
    }

    private async Task PersistResumeTokenAsync(string token)
    {
        try
        {
            using var userScope = BackgroundUserContext.BeginScope(_ownerUserId);
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<ISessionRepository>();
            await repo.UpdateResumeTokenAsync(_fleetSessionId, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogPersistFailed(_logger, _fleetSessionId, ex);
        }
    }

    private OutboxMessage CreateOutboxMessage(string sessionId, HarnessEvent harnessEvent, string createdAt)
    {
        return new OutboxMessage
        {
            Topic = $"session:{sessionId}",
            Type = harnessEvent.Type,
            Payload = harnessEvent.Payload!.Value.GetRawText(),
            UserId = _ownerUserId,
            CreatedAt = createdAt,
            AvailableAt = createdAt
        };
    }

    /// <summary>
    /// A conversation's messages by Claude's message id, and the message each tool call is in: the session's running
    /// turn, or a subagent's in its child session. Claude Code streams one content block per line and sends tool results
    /// as separate user lines, so a message is built up across lines before it is saved. Saved messages get Fleet's
    /// ascending ids: Claude's own ("msg_011C…") don't sort by time, and the conversation is ordered by id.
    /// </summary>
    private sealed class Conversation(string fleetSessionId)
    {
        public string FleetSessionId { get; } = fleetSessionId;
        public Dictionary<string, HarnessMessage> Messages { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> ToolCallMessageIds { get; } = new(StringComparer.Ordinal);

        /// <summary>Claude Code's own tool calls, by id, for what they did once they return.</summary>
        public Dictionary<string, ClaudeCodeToolUseBlock> ToolCalls { get; } = new(StringComparer.Ordinal);

        /// <summary>The message the model is writing, by Claude's id (<c>message_start</c>).</summary>
        public string? StreamingMessageId { get; set; }

        /// <summary>The text or thinking block the model is writing now, if any.</summary>
        public StreamingBlock? Streaming { get; set; }

        /// <summary>What a subagent was asked, once its child session shows it.</summary>
        public string? Prompt { get; set; }

        /// <summary>A subagent's task, once Claude Code has said which it is.</summary>
        public string? TaskId { get; set; }

        /// <summary>A subagent's child session reads as working.</summary>
        public bool Busy { get; set; }

        public void Clear()
        {
            Messages.Clear();
            ToolCallMessageIds.Clear();
            ToolCalls.Clear();
            StreamingMessageId = null;
            Streaming = null;
        }
    }

    /// <summary>An <c>AskUserQuestion</c> call waiting on the user: Claude Code's request, and what it asked.</summary>
    private sealed record PendingQuestion(string RequestId, System.Text.Json.JsonElement Input);

    /// <summary>
    /// A text or thinking block the model is writing: the part it becomes in Fleet's message, and the text so far.
    /// <see cref="Shown"/> once its first words went out.
    /// </summary>
    private sealed class StreamingBlock(string claudeMessageId, string kind, string partId)
    {
        public string ClaudeMessageId { get; } = claudeMessageId;
        public string Kind { get; } = kind;
        public string PartId { get; } = partId;
        public System.Text.StringBuilder Text { get; } = new();
        public bool Shown { get; set; }
    }

    /// <summary>What a claude process was started with, or switched to since.</summary>
    /// <param name="FleetTools">Which of Fleet's tools it has; null for none (Fleet can't be reached from it).</param>
    private sealed record ProcessSettings(string PermissionMode, string? Model, string? Effort, FleetToolSwitches? FleetTools = null);

    /// <summary>
    /// Which of Fleet's tools the session's next process gets: none for a subagent's child session, which never runs one, or
    /// when there's no Fleet address to give it. When the settings can't be read, a process keeps the tools it has.
    /// </summary>
    private async Task<FleetToolSwitches?> ReadFleetToolsAsync(FleetToolSwitches? current)
    {
        if (_readOnlyChild || _bridgeTokens is null || string.IsNullOrEmpty(_fleetUrl()))
            return null;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            return scope.ServiceProvider.GetService<FleetToolSettings>() is { } settings
                ? await settings.ForSessionAsync(_ownerUserId, _fleetSessionId).ConfigureAwait(false)
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            LogFleetToolsUnread(_logger, _fleetSessionId, ex);
            return current;
        }
    }

    /// <summary>A turn: from Fleet's prompt, or Claude Code's own <c>init</c>, to its <c>result</c>.</summary>
    private sealed class Turn
    {
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The process running it; null until Fleet's prompt is sent.</summary>
        public ClaudeCodeProcessManager? Process { get; set; }

        public ITimer? Timeout { get; set; }
    }
}

using System.Runtime.CompilerServices;
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
/// server stops, the next request attaches the session to the owner's new server.
/// </summary>
internal sealed partial class OpenCode2HarnessSession : IHarnessSession, IOpenCode2EventSink
{
    public const string Type = "opencode2";

    private readonly OpenCode2SessionContext _context;
    private readonly Func<CancellationToken, Task<OpenCode2Server>> _servers;
    private readonly OpenCode2Mapper _mapper;
    private readonly IAnalyticsCollector? _analytics;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _attachLock = new(1, 1);
    private readonly Channel<HarnessEvent> _events = Channel.CreateBounded<HarnessEvent>(new BoundedChannelOptions(1000)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = false,
        SingleWriter = false,
    });

    private OpenCode2Server? _server;
    private volatile HarnessSessionStatus _status = HarnessSessionStatus.Idle;
    private bool _disposed;

    /// <param name="servers">The owner's running server, started when there's none.</param>
    internal OpenCode2HarnessSession(
        string instanceId,
        string harnessSessionId,
        OpenCode2SessionContext context,
        OpenCode2Server server,
        Func<CancellationToken, Task<OpenCode2Server>> servers,
        IAnalyticsCollector? analytics,
        ILogger logger)
    {
        InstanceId = instanceId;
        ResumeToken = harnessSessionId;
        _context = context;
        _servers = servers;
        _analytics = analytics;
        _logger = logger;
        _mapper = new OpenCode2Mapper(context.FleetSessionId);
        Attach(server);
    }

    public string InstanceId { get; }

    public int? ProcessId => _server?.ProcessId;

    /// <summary>The V2 session id (<c>ses_…</c>).</summary>
    public string ResumeToken { get; }

    public string HarnessType => Type;

    public HarnessSessionStatus Status => _status;

    public async Task SendPromptAsync(string text, PromptOptions? options, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (options?.Attachments is { Count: > 0 })
            throw new NotSupportedException("OpenCode 2 sessions in Fleet don't take attachments yet.");

        var server = await AttachedServerAsync(ct).ConfigureAwait(false);
        LogPrompt(_logger, InstanceId);

        // The user message keeps the id Fleet showed it with (V2 takes ids of the same msg_ form).
        // The status follows V2's execution events: a short turn can be over before this request returns.
        await server.Client.PromptAsync(ResumeToken, text, options?.MessageId, ct).ConfigureAwait(false);
    }

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
            return active.Contains(ResumeToken) ? ActivityStatuses.Busy : ActivityStatuses.Idle;
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

    /// <summary>History is read from Fleet's own store until V2's message list is mapped.</summary>
    public Task<MessagePage> GetMessagesAsync(MessageQuery? query, CancellationToken ct)
        => Task.FromResult(new MessagePage([], HasMore: false));

    public Task<string?> AskOffTheRecordAsync(string prompt, CancellationToken ct)
        => Task.FromResult<string?>(null);

    public Task SendCommandAsync(CommandOptions options, CancellationToken ct)
        => throw new NotSupportedException("OpenCode 2 sessions in Fleet don't run commands yet.");

    public Task AnswerQuestionAsync(string requestId, IReadOnlyList<IReadOnlyList<string>> answers, CancellationToken ct)
        => throw new NotSupportedException("OpenCode 2 sessions in Fleet don't answer questions yet.");

    public Task RejectQuestionAsync(string requestId, CancellationToken ct)
        => throw new NotSupportedException("OpenCode 2 sessions in Fleet don't answer questions yet.");

    public Task<IReadOnlyList<AgentInfo>> GetAgentsAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<AgentInfo>>([]);

    public Task<IReadOnlyList<CommandInfo>> GetCommandsAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<CommandInfo>>([]);

    public Task<IReadOnlyList<ProviderInfo>> GetProvidersAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ProviderInfo>>([]);

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

        switch (evt.Type)
        {
            case "session.execution.started":
                SetStatus(HarnessSessionStatus.Running);
                break;
            case "session.execution.succeeded" or "session.execution.failed" or "session.execution.interrupted":
                SetStatus(HarnessSessionStatus.Idle);
                break;
        }

        foreach (var harnessEvent in _mapper.Map(evt))
            _events.Writer.TryWrite(harnessEvent);
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
            return current;

        await _attachLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_server is { IsRunning: true } attached)
                return attached;

            var server = await _servers(ct).ConfigureAwait(false);
            if (await server.Client.GetSessionAsync(ResumeToken, ct).ConfigureAwait(false) is null)
                throw new InvalidOperationException($"OpenCode 2 has no session {ResumeToken} any more.");

            Attach(server);
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

    private void SetStatus(HarnessSessionStatus status)
    {
        if (_status is not (HarnessSessionStatus.Stopping or HarnessSessionStatus.Stopped))
            _status = status;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Sending a prompt to OpenCode 2 session {InstanceId}")]
    private static partial void LogPrompt(ILogger logger, string instanceId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Interrupting OpenCode 2 session {InstanceId}")]
    private static partial void LogAbort(ILogger logger, string instanceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't interrupt OpenCode 2 session {InstanceId} while stopping it")]
    private static partial void LogStopInterruptFailed(ILogger logger, string instanceId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't delete OpenCode 2 session {InstanceId} from its server")]
    private static partial void LogDeleteFailed(ILogger logger, string instanceId, Exception exception);
}

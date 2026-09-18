using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode2;

/// <summary>Where a server sends the events of one V2 session.</summary>
internal interface IOpenCode2EventSink
{
    void OnEvent(OpenCode2Event evt);

    /// <summary>The server stopped; nothing more will arrive from it.</summary>
    void OnServerStopped();
}

/// <summary>
/// One <c>opencode2 serve</c> process that Fleet runs for one owner, serving every directory: V2 takes the directory
/// per session (<c>location</c>), so there's no process per folder. Its single event stream carries every session's
/// events and is routed to the attached sessions by <c>sessionID</c>. Events for sessions nobody attached are dropped.
/// </summary>
internal sealed partial class OpenCode2Server : IAsyncDisposable
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(1);

    private readonly OpenCode2ProcessManager? _process;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, IOpenCode2EventSink> _sinks = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _pump;
    private TaskCompletionSource _connected = NewConnectedSource();
    private int _stopped;

    internal OpenCode2Server(
        string ownerUserId,
        OpenCode2HttpClient client,
        string bridgeToken,
        OpenCode2ProcessManager? process,
        ILogger logger)
    {
        OwnerUserId = ownerUserId;
        Client = client;
        BridgeToken = bridgeToken;
        _process = process;
        _logger = logger;

        if (_process is not null)
            _process.Exited += (_, _) => _ = StopRoutingAsync();

        _pump = Task.Run(PumpAsync, CancellationToken.None);
    }

    public string OwnerUserId { get; }

    public OpenCode2HttpClient Client { get; }

    /// <summary>Set as <c>FLEET_BRIDGE_TOKEN</c> in the process, and in <c>FLEET_URL</c>, so Fleet knows its calls.</summary>
    public string BridgeToken { get; }

    public int? ProcessId => _process?.ProcessId;

    /// <summary>False once the process exited or Fleet stopped it; a stopped server is replaced, not restarted.</summary>
    public bool IsRunning => Volatile.Read(ref _stopped) == 0 && _process?.IsRunning != false;

    /// <summary>Completes when the event stream is open, so events that follow a request aren't missed.</summary>
    public Task WaitForEventsAsync(CancellationToken ct) => Volatile.Read(ref _connected).Task.WaitAsync(ct);

    public void Attach(string harnessSessionId, IOpenCode2EventSink sink) => _sinks[harnessSessionId] = sink;

    public void Detach(string harnessSessionId, IOpenCode2EventSink sink)
        => _sinks.TryRemove(new KeyValuePair<string, IOpenCode2EventSink>(harnessSessionId, sink));

    private async Task PumpAsync()
    {
        var ct = _stopping.Token;
        while (!ct.IsCancellationRequested && IsRunning)
        {
            try
            {
                await foreach (var evt in Client.ReadEventsAsync(OnConnected, ct).ConfigureAwait(false))
                {
                    if (evt.SessionId is { } sessionId && _sinks.TryGetValue(sessionId, out var sink))
                        Deliver(sink, evt);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogStreamFailed(_logger, ProcessId ?? 0, ex);
            }

            // The stream ended with the process still up: open it again, and make new waiters wait for that.
            // Anything sent in between is lost (V2 doesn't replay).
            if (Volatile.Read(ref _connected).Task.IsCompleted)
                Volatile.Write(ref _connected, NewConnectedSource());

            try
            {
                await Task.Delay(ReconnectDelay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void OnConnected()
    {
        LogConnected(_logger, ProcessId ?? 0);
        Volatile.Read(ref _connected).TrySetResult();
    }

    private void Deliver(IOpenCode2EventSink sink, OpenCode2Event evt)
    {
        try
        {
            sink.OnEvent(evt);
        }
        catch (Exception ex)
        {
            // One session's bad event must not stop the stream for every other session.
            LogSinkFailed(_logger, evt.Type, ex);
        }
    }

    /// <summary>Stops routing and tells every attached session the server is gone.</summary>
    private async Task StopRoutingAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 1)
            return;

        await _stopping.CancelAsync().ConfigureAwait(false);
        Volatile.Read(ref _connected).TrySetException(new InvalidOperationException("The OpenCode 2 server stopped."));
        foreach (var sink in _sinks.Values)
        {
            try
            {
                sink.OnServerStopped();
            }
            catch (Exception ex)
            {
                LogSinkFailed(_logger, "server stopped", ex);
            }
        }
        _sinks.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await StopRoutingAsync().ConfigureAwait(false);
        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }

        if (_process is not null)
            await _process.DisposeAsync().ConfigureAwait(false);
        Client.Dispose();
        _stopping.Dispose();
    }

    private static TaskCompletionSource NewConnectedSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [LoggerMessage(Level = LogLevel.Debug, Message = "OpenCode 2 server {ProcessId}: event stream open")]
    private static partial void LogConnected(ILogger logger, int processId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "OpenCode 2 server {ProcessId}: event stream failed; reconnecting")]
    private static partial void LogStreamFailed(ILogger logger, int processId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "An OpenCode 2 session failed to handle {EventType}")]
    private static partial void LogSinkFailed(ILogger logger, string eventType, Exception exception);
}

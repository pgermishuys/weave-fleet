using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace WeaveFleet.Application.Terminals;

/// <summary>
/// One running shell: reads its output, keeps the cleaned scrollback, saves it shortly after it changes,
/// and hands every attached client the raw output in order.
/// </summary>
internal sealed partial class LiveTerminal : IAsyncDisposable
{
    /// <summary>Frames a client may have waiting before it counts as too slow (each at most 16 KB).</summary>
    private const int ClientQueueFrames = 64;
    private const int ReadBufferSize = 16 * 1024;
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(250);

    private readonly Lock _gate = new();
    private readonly TerminalHistory _history;
    private readonly TerminalReplaySanitizer _sanitizer = new();
    private readonly List<Channel<TerminalFrame>> _clients = [];
    private readonly IPtyProcess _process;
    private readonly ITerminalHistoryStore _store;
    private readonly ILogger _logger;
    private readonly Func<LiveTerminal, PtyExit, Task> _onExited;
    private readonly Timer _saveTimer;
    private bool _saveQueued;
    private bool _ended;
    private Task _readLoop = Task.CompletedTask;
    private volatile Task _lastSave = Task.CompletedTask;
    private int _resourcesReleased;

    public LiveTerminal(
        TerminalContext context,
        string id,
        string title,
        DateTimeOffset createdAt,
        IPtyProcess process,
        TerminalHistory history,
        ITerminalHistoryStore store,
        ILogger logger,
        Func<LiveTerminal, PtyExit, Task> onExited)
    {
        Context = context;
        Id = id;
        Title = title;
        CreatedAt = createdAt;
        _process = process;
        _history = history;
        _store = store;
        _logger = logger;
        _onExited = onExited;
        _saveTimer = new Timer(_ => _lastSave = SaveNowAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public TerminalContext Context { get; }
    public string Id { get; }
    public string Title { get; }
    public DateTimeOffset CreatedAt { get; }

    public TerminalInfo Info => new(Id, Title, TerminalStatus.Running, CreatedAt);

    public void Start() => _readLoop = Task.Run(ReadLoopAsync);

    /// <summary>
    /// Adds a client. Its first frames are the saved scrollback, then live output from that point on,
    /// with nothing missed or repeated in between.
    /// </summary>
    public TerminalAttachment Attach()
    {
        var channel = Channel.CreateBounded<TerminalFrame>(new BoundedChannelOptions(ClientQueueFrames)
        {
            SingleReader = true,
            SingleWriter = false,
        });

        byte[] replay;
        lock (_gate)
        {
            replay = _history.ToArray();
            if (_ended)
                channel.Writer.TryComplete();
            else
                _clients.Add(channel);
        }

        return new TerminalAttachment(this, channel, replay);
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) => _process.WriteAsync(data, ct);

    public void Resize(int cols, int rows) => _process.Resize(Math.Clamp(cols, 2, 1000), Math.Clamp(rows, 1, 500));

    public void Clear()
    {
        lock (_gate)
        {
            _history.Clear();
            foreach (var client in _clients)
                client.Writer.TryWrite(TerminalFrame.Cleared);
        }
        QueueSave();
    }

    internal void Detach(Channel<TerminalFrame> channel)
    {
        lock (_gate)
            _clients.Remove(channel);
        channel.Writer.TryComplete();
    }

    /// <summary>
    /// Ends the shell without saving, for a terminal that's being closed for good. When this returns, no save
    /// is running or queued, so the caller can delete the scrollback.
    /// </summary>
    public async Task KillAsync()
    {
        StopSaving();
        _process.Kill();
        await _readLoop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await _lastSave.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await ReleaseResourcesAsync().ConfigureAwait(false);
    }

    /// <summary>Ends the shell, then saves the scrollback; used when Fleet shuts down.</summary>
    public async Task StopForShutdownAsync()
    {
        StopSaving();
        _process.Kill();
        await _readLoop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await _lastSave.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await SaveNowAsync().ConfigureAwait(false);
        await ReleaseResourcesAsync().ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => new(KillAsync());

    private void StopSaving()
    {
        try
        {
            _saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task ReleaseResourcesAsync()
    {
        if (Interlocked.Exchange(ref _resourcesReleased, 1) != 0)
            return;
        await _saveTimer.DisposeAsync().ConfigureAwait(false);
        await _process.DisposeAsync().ConfigureAwait(false);
    }

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[ReadBufferSize];
        try
        {
            int read;
            while ((read = await _process.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                var chunk = buffer.AsSpan(0, read).ToArray();
                lock (_gate)
                {
                    _history.Append(_sanitizer.Process(chunk));
                    Publish(TerminalFrame.Output(chunk));
                }
                QueueSave();
            }
        }
        catch (Exception ex)
        {
            LogReadFailed(_logger, ex, Id);
        }

        var exit = await _process.Exited.ConfigureAwait(false);
        lock (_gate)
        {
            _history.Append(_sanitizer.Flush());
            _ended = true;
            Publish(TerminalFrame.Exited(exit.ExitCode));
            foreach (var client in _clients)
                client.Writer.TryComplete();
            _clients.Clear();
        }

        if (exit.Killed)
            return; // Whoever killed it releases it: KillAsync or StopForShutdownAsync.

        // The shell ended on its own and the terminal is about to be removed, so don't save it again.
        StopSaving();
        await _lastSave.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        try
        {
            await _onExited(this, exit).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogExitHandlerFailed(_logger, ex, Id);
        }
        await ReleaseResourcesAsync().ConfigureAwait(false);
    }

    /// <summary>Hands a frame to every client. A client whose queue is full is dropped. Call under the lock.</summary>
    private void Publish(TerminalFrame frame)
    {
        for (var i = _clients.Count - 1; i >= 0; i--)
        {
            var client = _clients[i];
            if (client.Writer.TryWrite(frame))
                continue;
            client.Writer.TryComplete(new TerminalClientTooSlowException());
            _clients.RemoveAt(i);
        }
    }

    private void QueueSave()
    {
        lock (_gate)
        {
            if (_saveQueued)
                return;
            _saveQueued = true;
        }
        try
        {
            _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task SaveNowAsync()
    {
        byte[] snapshot;
        lock (_gate)
        {
            _saveQueued = false;
            snapshot = _history.ToArray();
        }

        try
        {
            await _store.WriteHistoryAsync(Context.SessionId, Id, snapshot).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogSaveFailed(_logger, ex, Id);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reading terminal {TerminalId} failed")]
    private static partial void LogReadFailed(ILogger logger, Exception ex, string terminalId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Closing terminal {TerminalId} after its shell ended failed")]
    private static partial void LogExitHandlerFailed(ILogger logger, Exception ex, string terminalId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Saving scrollback for terminal {TerminalId} failed")]
    private static partial void LogSaveFailed(ILogger logger, Exception ex, string terminalId);
}

/// <summary>
/// One client attached to a terminal. Read <see cref="Replay"/> first, then <see cref="Frames"/> until it
/// completes. Dispose to detach.
/// </summary>
public sealed class TerminalAttachment : IDisposable
{
    private readonly LiveTerminal _terminal;
    private readonly Channel<TerminalFrame> _channel;
    private int _disposed;

    internal TerminalAttachment(LiveTerminal terminal, Channel<TerminalFrame> channel, byte[] replay)
    {
        _terminal = terminal;
        _channel = channel;
        Replay = replay;
    }

    public TerminalInfo Terminal => _terminal.Info;

    /// <summary>The saved scrollback at the moment of attaching.</summary>
    public byte[] Replay { get; }

    /// <summary>Everything after <see cref="Replay"/>. Completes when the shell ends or the client is dropped.</summary>
    public ChannelReader<TerminalFrame> Frames => _channel.Reader;

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) => _terminal.WriteAsync(data, ct);

    public void Resize(int cols, int rows) => _terminal.Resize(cols, rows);

    public void Clear() => _terminal.Clear();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _terminal.Detach(_channel);
    }
}

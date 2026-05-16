using System.Globalization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace WeaveFleet.Api.Telemetry;

/// <summary>
/// An <see cref="ILoggerProvider"/> that writes log entries to daily rolling plain-text files.
/// Fully AOT/trimming compatible — no reflection, no dynamic code.
/// </summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _logDirectory;
    private readonly string _filePrefix;
    private readonly LogLevel _minimumLevel;

    // Bounded channel used as an async queue between logger callers and the drain task.
    // Drop-oldest on overflow to avoid blocking the hot path under burst load.
    private readonly Channel<string> _channel;
    private readonly Task _drainTask;
    private readonly CancellationTokenSource _cts = new();

    private StreamWriter? _writer;
    private DateOnly _currentDate;
    private bool _disposed;

    internal const int ChannelCapacity = 4096;

    public FileLoggerProvider(string logDirectory, string filePrefix, LogLevel minimumLevel)
    {
        _logDirectory = logDirectory;
        _filePrefix = filePrefix;
        _minimumLevel = minimumLevel;

        _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        _drainTask = Task.Run(DrainAsync);
    }

    public ILogger CreateLogger(string categoryName)
        => new FileLogger(categoryName, _minimumLevel, _channel.Writer);

    private async Task DrainAsync()
    {
        try
        {
            await foreach (var line in _channel.Reader.ReadAllAsync(_cts.Token))
            {
                await EnsureWriterAsync();
                await _writer!.WriteLineAsync(line);
                // Flush after each line for diagnostic reliability (log must survive crashes).
                await _writer.FlushAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown — fall through to final flush.
        }
        finally
        {
            // Drain remaining items after cancellation.
            while (_channel.Reader.TryRead(out var remaining))
            {
                await EnsureWriterAsync();
                await _writer!.WriteLineAsync(remaining);
            }

            if (_writer is not null)
            {
                await _writer.FlushAsync();
                await _writer.DisposeAsync();
                _writer = null;
            }
        }
    }

    private async ValueTask EnsureWriterAsync()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (_writer is not null && today == _currentDate)
            return;

        // Date rolled — close old writer, open new file.
        if (_writer is not null)
        {
            await _writer.FlushAsync();
            await _writer.DisposeAsync();
        }

        _currentDate = today;
        var fileName = $"{_filePrefix}-{today:yyyy-MM-dd}.log";
        var path = Path.Combine(_logDirectory, fileName);

        // Append mode so re-runs on the same day don't overwrite earlier entries.
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read,
            bufferSize: 4096, useAsync: true);
        _writer = new StreamWriter(stream) { AutoFlush = false };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        _channel.Writer.TryComplete();

        // Wait (with timeout) for the drain task to finish flushing.
        _drainTask.Wait(TimeSpan.FromSeconds(5));
        _cts.Dispose();
    }
}

/// <summary>
/// An <see cref="ILogger"/> that enqueues formatted log lines to the provider's channel.
/// </summary>
internal sealed class FileLogger : ILogger
{
    private readonly string _categoryName;
    private readonly LogLevel _minimumLevel;
    private readonly ChannelWriter<string> _writer;

    public FileLogger(string categoryName, LogLevel minimumLevel, ChannelWriter<string> writer)
    {
        _categoryName = categoryName;
        _minimumLevel = minimumLevel;
        _writer = writer;
    }

    public bool IsEnabled(LogLevel logLevel) => logLevel >= _minimumLevel;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var message = formatter(state, exception);
        var levelShort = GetLevelShort(logLevel);
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        var line = $"{timestamp} [{levelShort}] [{_categoryName}] {message}";

        if (exception is not null)
            line += Environment.NewLine + exception.ToString();

        // TryWrite is non-blocking. If the channel is full, BoundedChannelFullMode.DropOldest
        // handles overflow on the reader side.
        _writer.TryWrite(line);
    }

    private static string GetLevelShort(LogLevel level) => level switch
    {
        LogLevel.Trace       => "TRC",
        LogLevel.Debug       => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning     => "WRN",
        LogLevel.Error       => "ERR",
        LogLevel.Critical    => "CRT",
        _                    => "???"
    };
}

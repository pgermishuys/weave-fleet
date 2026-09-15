namespace WeaveFleet.Api.Desktop;

/// <summary>
/// Stops Fleet when its standard input closes. The desktop app starts Fleet with a pipe on standard input and
/// never writes to it. When the app exits, however it exits, the operating system closes the pipe and Fleet shuts
/// down with it, stopping its agents and releasing the database lock. Only runs in desktop mode: a Fleet started
/// with standard input on <c>/dev/null</c> would stop straight away.
/// </summary>
internal sealed partial class StandardInputWatcher(
    Func<Stream> openInput,
    IHostApplicationLifetime lifetime,
    ILogger<StandardInputWatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var input = openInput();

        // Reads block, and a console stream can't cancel one, so the read gets its own thread and shutdown
        // stops waiting for it instead.
        var closed = Task.Factory.StartNew(
            () => ReadToEnd(input),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        try
        {
            await closed.WaitAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        LogInputClosed();
        lifetime.StopApplication();
    }

    private static void ReadToEnd(Stream input)
    {
        var buffer = new byte[256];
        try
        {
            while (input.Read(buffer) > 0)
            {
            }
        }
        catch (IOException)
        {
            // A broken pipe is the app going away too.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "The desktop app closed Fleet's standard input — shutting down.")]
    private partial void LogInputClosed();
}

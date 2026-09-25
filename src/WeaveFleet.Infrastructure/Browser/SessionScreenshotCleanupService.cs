using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WeaveFleet.Infrastructure.Browser;

/// <summary>
/// Deletes agent screenshots older than <see cref="SessionScreenshotStore.Retention"/>, once at startup and then
/// every <see cref="Interval"/>. An archived session's shots go too: the conversation stays, the pictures don't.
/// </summary>
internal sealed partial class SessionScreenshotCleanupService(
    SessionScreenshotStore store,
    TimeProvider time,
    ILogger<SessionScreenshotCleanupService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var deleted = store.DeleteOlderThan(time.GetUtcNow() - SessionScreenshotStore.Retention);
                if (deleted > 0)
                    LogDeleted(deleted);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogFailed(ex);
            }

            try
            {
                await Task.Delay(Interval, time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Count} screenshot(s) older than a week")]
    private partial void LogDeleted(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Clearing old screenshots failed; trying again later")]
    private partial void LogFailed(Exception ex);
}

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Configuration;

namespace WeaveFleet.Infrastructure.Services;

public sealed partial class OutboxDispatchBackgroundService(
    InProcessOutboxDispatcher dispatcher,
    FleetOptions options,
    ILogger<OutboxDispatchBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var dispatched = await dispatcher.DispatchAvailableAsync(stoppingToken).ConfigureAwait(false);
                if (dispatched > 0)
                    continue;

                var wasSignaled = await dispatcher.WaitForSignalAsync(stoppingToken).ConfigureAwait(false);
                if (!wasSignaled)
                {
                    var emptyPollSleep = Math.Max(0, options.Outbox.EmptyPollSleepMilliseconds);
                    if (emptyPollSleep > 0)
                        await Task.Delay(TimeSpan.FromMilliseconds(emptyPollSleep), stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogDispatchFailed(ex);
                var emptyPollSleep = Math.Max(1, options.Outbox.EmptyPollSleepMilliseconds);
                await Task.Delay(TimeSpan.FromMilliseconds(emptyPollSleep), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox dispatch background service started.")]
    private partial void LogStarted();

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox dispatch loop failed.")]
    private partial void LogDispatchFailed(Exception ex);
}

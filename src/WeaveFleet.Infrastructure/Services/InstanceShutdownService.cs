using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Infrastructure.Services;

/// <summary>
/// Hosted service that gracefully stops all tracked harness instances on shutdown.
/// Registered after <see cref="HarnessEventRelay"/> so that it stops first (reverse order),
/// giving relay pumps a chance to observe disconnection and flush buffered deltas before
/// the relay itself shuts down.
/// </summary>
internal sealed class InstanceShutdownService(
    InstanceTracker tracker,
    ILogger<InstanceShutdownService> logger) : IHostedService
{
    private static readonly Action<ILogger, int, Exception?> LogShutdownStarted =
        LoggerMessage.Define<int>(LogLevel.Information, new EventId(1, "GracefulShutdownStarted"),
            "Graceful shutdown: stopping {Count} tracked instance(s).");

    private static readonly Action<ILogger, string, Exception?> LogInstanceFailed =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(2, "GracefulShutdownInstanceFailed"),
            "Graceful shutdown: failed to stop instance {InstanceId}.");

    private static readonly Action<ILogger, int, Exception?> LogShutdownComplete =
        LoggerMessage.Define<int>(LogLevel.Information, new EventId(3, "GracefulShutdownComplete"),
            "Graceful shutdown: stopped {Count} instance(s).");

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var instances = tracker.GetAll();
        if (instances.Count == 0)
        {
            return;
        }

        LogShutdownStarted(logger, instances.Count, null);

        var tasks = instances.Values.Select(async session =>
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(5));
                await session.StopAsync(cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogInstanceFailed(logger, session.InstanceId, ex);
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        LogShutdownComplete(logger, instances.Count, null);
    }
}

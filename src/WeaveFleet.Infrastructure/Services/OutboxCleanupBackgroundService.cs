using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Services;

public sealed partial class OutboxCleanupBackgroundService(
    IServiceScopeFactory scopeFactory,
    FleetOptions options,
    ILogger<OutboxCleanupBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var outboxRepository = scope.ServiceProvider.GetRequiredService<IOutboxRepository>();
                var deleted = await outboxRepository.DeleteDispatchedBeforeAsync(
                    DateTimeOffset.UtcNow.AddHours(-Math.Max(1, options.Outbox.RetentionHours)).ToString("O"),
                    Math.Max(1, options.Outbox.CleanupBatchSize)).ConfigureAwait(false);

                if (deleted > 0)
                    LogCleanupDeleted(deleted);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogCleanupFailed(ex);
            }

            await Task.Delay(
                TimeSpan.FromMinutes(Math.Max(1, options.Outbox.CleanupIntervalMinutes)),
                stoppingToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox cleanup background service started.")]
    private partial void LogStarted();

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Count} dispatched outbox row(s).")]
    private partial void LogCleanupDeleted(int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox cleanup loop failed.")]
    private partial void LogCleanupFailed(Exception ex);
}

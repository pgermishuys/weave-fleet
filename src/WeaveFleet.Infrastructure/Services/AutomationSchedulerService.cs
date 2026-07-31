using System.Collections.Concurrent;
using Cronos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Services;

public sealed partial class AutomationSchedulerService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AutomationSchedulerService> _logger;
    private readonly ConcurrentDictionary<string, int> _runningCounts = new();

    public AutomationSchedulerService(IServiceScopeFactory scopeFactory, ILogger<AutomationSchedulerService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollAndExecuteAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogSchedulerPollError(ex);
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task PollAndExecuteAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAutomationRepository>();
        var automations = await repo.ListEnabledByTriggerTypeAsync("schedule");

        var now = DateTimeOffset.UtcNow;
        var windowStart = now - PollInterval;

        foreach (var automation in automations)
        {
            try
            {
                var cron = CronExpression.Parse(automation.TriggerConfig);
                var nextOccurrence = cron.GetNextOccurrence(windowStart.UtcDateTime, TimeZoneInfo.Utc, inclusive: true);

                if (nextOccurrence is null || nextOccurrence > now.UtcDateTime) continue;

                var currentRuns = _runningCounts.GetValueOrDefault(automation.Id, 0);
                if (currentRuns >= automation.MaxConcurrentRuns)
                {
                    LogMaxConcurrentReached(automation.Id, automation.MaxConcurrentRuns);
                    continue;
                }

                _runningCounts.AddOrUpdate(automation.Id, 1, (_, c) => c + 1);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var execScope = _scopeFactory.CreateScope();
                        var execService = execScope.ServiceProvider.GetRequiredService<AutomationExecutionService>();
                        await execService.ExecuteAsync(automation, ct: ct);
                    }
                    catch (Exception ex)
                    {
                        LogExecutionFailed(ex, automation.Id);
                    }
                    finally
                    {
                        _runningCounts.AddOrUpdate(automation.Id, 0, (_, c) => Math.Max(0, c - 1));
                    }
                }, ct);
            }
            catch (CronFormatException ex)
            {
                LogInvalidCronExpression(ex, automation.Id, automation.TriggerConfig);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Error in automation scheduler poll")]
    private partial void LogSchedulerPollError(Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Automation {AutomationId} at max concurrent runs ({MaxRuns}), skipping")]
    private partial void LogMaxConcurrentReached(string automationId, int maxRuns);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to execute scheduled automation {AutomationId}")]
    private partial void LogExecutionFailed(Exception ex, string automationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Invalid cron expression for automation {AutomationId}: {CronConfig}")]
    private partial void LogInvalidCronExpression(Exception ex, string automationId, string cronConfig);
}

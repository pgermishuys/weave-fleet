using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Usage;
using WeaveFleet.Infrastructure.Users;

namespace WeaveFleet.Infrastructure.Usage;

/// <summary>Every 30 seconds, asks the providers behind a busy session whose answer is old enough (<see cref="UsageLimitsService.BusyEvery"/>). Nothing while no session is working.</summary>
internal sealed partial class UsagePollingService(UsageLimitsService usage, TimeProvider time, ILogger<UsagePollingService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await TickAsync(stoppingToken).ConfigureAwait(false);
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

    internal async Task TickAsync(CancellationToken ct)
    {
        try
        {
            foreach (var userId in usage.BusyUsers())
            {
                using var user = BackgroundUserContext.BeginScope(userId);
                await usage.PollBusyAsync(userId, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogTickFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Polling usage limits failed; trying again shortly")]
    private partial void LogTickFailed(Exception ex);
}

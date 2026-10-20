using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Machines;
using WeaveFleet.Application.Updates;
using WeaveFleet.Infrastructure.Users;

namespace WeaveFleet.Infrastructure.Updates;

/// <summary>
/// Restarts this Fleet into the update it has downloaded, by itself, when its rule says it may: <c>idle</c> once no
/// session has worked for a minute, <c>overnight</c> the same but only between 02:00 and 05:00 on this machine's clock.
/// It is the same restart a click on Restart starts (<see cref="UpdateRestartService.BeginAsync"/>), begun by a timer.
/// </summary>
public sealed partial class SelfUpdateService(
    UpdateRuleStore rules,
    UpdateRestartService restart,
    IServiceScopeFactory scopes,
    TimeProvider time,
    ILogger<SelfUpdateService> logger,
    // For tests: how many sessions are working.
    Func<Task<int>>? workingSessions = null) : BackgroundService
{
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(15);

    /// <summary>How long nothing must have worked before a restart.</summary>
    public static readonly TimeSpan QuietFor = TimeSpan.FromMinutes(1);

    public static readonly TimeSpan NightStart = TimeSpan.FromHours(2);
    public static readonly TimeSpan NightEnd = TimeSpan.FromHours(5);

    private DateTimeOffset? _lastWorking;

    /// <summary>Whether the rule lets a restart happen now. Pure, so the table of cases is a table of tests.</summary>
    public static bool MayRestart(string? rule, TimeSpan quietFor, TimeSpan localTimeOfDay)
        => quietFor >= QuietFor && rule switch
        {
            UpdateRules.Idle => true,
            UpdateRules.Overnight => localTimeOfDay >= NightStart && localTimeOfDay < NightEnd,
            _ => false,
        };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _lastWorking = time.GetUtcNow();
        using var timer = new PeriodicTimer(Tick, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await TickAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>One look: remembers whether anything is working, and restarts when the rule allows. Returns whether it began a restart.</summary>
    public async Task<bool> TickAsync()
    {
        var now = time.GetUtcNow();
        _lastWorking ??= now;
        try
        {
            using var background = BackgroundUserContext.BeginScope("local-user");
            if (await WorkingAsync() > 0)
            {
                _lastWorking = now;
                return false;
            }

            var rule = rules.Rule;
            if (!UpdateRules.IsKnown(rule) || rule == UpdateRules.Ask || restart.PendingUpdate() is not { } staged || restart.RolledBack()?.To == staged)
                return false;

            if (!restart.Capability().CanRestart || !MayRestart(rule, now - _lastWorking.Value, time.GetLocalNow().TimeOfDay))
                return false;

            var result = await restart.BeginAsync(rule!);
            if (result.Outcome == RestartOutcome.Accepted)
                LogSelfUpdate(rule!, staged);
            return result.Outcome == RestartOutcome.Accepted;
        }
        catch (Exception ex)
        {
            LogTickFailed(ex);
            return false;
        }
    }

    private async Task<int> WorkingAsync()
    {
        if (workingSessions is not null)
            return await workingSessions();

        using var scope = scopes.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<MachineCapabilitiesReader>().ReadAsync()).Sessions.Working;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Updating itself ({Rule} rule): restarting into v{To}.")]
    private partial void LogSelfUpdate(string rule, string to);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Checking whether to update itself failed.")]
    private partial void LogTickFailed(Exception ex);
}

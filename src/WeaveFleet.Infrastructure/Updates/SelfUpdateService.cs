using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Machines;
using WeaveFleet.Application.Updates;
using WeaveFleet.Infrastructure.Users;

namespace WeaveFleet.Infrastructure.Updates;

/// <summary>
/// Restarts this Fleet into the update it has downloaded, by itself, when its rule says it may: <c>idle</c> once
/// nothing has been in use for 15 minutes (no turn running, no question waiting, no prompt or answer from any device),
/// <c>overnight</c> the same but after 2 quiet minutes, and only between 02:00 and 05:00 on this machine's clock.
/// It is the same restart a click on Restart starts (<see cref="UpdateRestartService.BeginAsync"/>), begun by a timer.
/// </summary>
public sealed partial class SelfUpdateService(
    UpdateRuleStore rules,
    UpdateRestartService restart,
    IServiceScopeFactory scopes,
    TimeProvider time,
    ILogger<SelfUpdateService> logger,
    // For tests: how many sessions are working or waiting for an answer.
    Func<Task<MachineSessionCounts>>? workingSessions = null,
    UserActivityClock? activity = null) : BackgroundService
{
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(15);

    /// <summary>How long nothing must have been in use before the <c>idle</c> rule restarts.</summary>
    public static readonly TimeSpan QuietFor = TimeSpan.FromMinutes(15);

    /// <summary>The same for <c>overnight</c>, whose window already says nobody is around.</summary>
    public static readonly TimeSpan OvernightQuietFor = TimeSpan.FromMinutes(2);

    public static readonly TimeSpan NightStart = TimeSpan.FromHours(2);
    public static readonly TimeSpan NightEnd = TimeSpan.FromHours(5);

    /// <summary>For a live check of the timings without waiting 15 minutes: replaces both quiet times, in seconds.</summary>
    public const string QuietSecondsVariable = "FLEET_TEST_UPDATE_QUIET_SECONDS";

    private static readonly TimeSpan? QuietOverride =
        double.TryParse(Environment.GetEnvironmentVariable(QuietSecondsVariable), System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : null;

    private DateTimeOffset? _lastWorking;

    /// <summary>Whether the rule lets a restart happen now. Pure, so the table of cases is a table of tests.</summary>
    public static bool MayRestart(string? rule, TimeSpan quietFor, TimeSpan localTimeOfDay, TimeSpan? quietOverride = null)
        => rule switch
        {
            UpdateRules.Idle => quietFor >= (quietOverride ?? QuietFor),
            UpdateRules.Overnight => quietFor >= (quietOverride ?? OvernightQuietFor) && localTimeOfDay >= NightStart && localTimeOfDay < NightEnd,
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

    /// <summary>One look: remembers whether anything is in use, and restarts when the rule allows. Returns whether it began a restart.</summary>
    public async Task<bool> TickAsync()
    {
        var now = time.GetUtcNow();
        _lastWorking ??= now;
        try
        {
            using var background = BackgroundUserContext.BeginScope("local-user");
            if ((await WorkingAsync()).Busy > 0)
            {
                _lastWorking = now;
                return false;
            }

            var rule = rules.Rule;
            if (!UpdateRules.IsKnown(rule) || rule == UpdateRules.Ask || restart.PendingUpdate() is not { } staged || restart.RolledBack()?.To == staged)
                return false;

            if (!restart.Capability().CanRestart || !MayRestart(rule, now - LastInUse(_lastWorking.Value), time.GetLocalNow().TimeOfDay, QuietOverride))
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

    private DateTimeOffset LastInUse(DateTimeOffset lastWorking)
        => activity?.Last is { } sent && sent > lastWorking ? sent : lastWorking;

    private async Task<MachineSessionCounts> WorkingAsync()
    {
        if (workingSessions is not null)
            return await workingSessions();

        using var scope = scopes.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<MachineCapabilitiesReader>().ReadAsync()).Sessions;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Updating itself ({Rule} rule): restarting into v{To}.")]
    private partial void LogSelfUpdate(string rule, string to);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Checking whether to update itself failed.")]
    private partial void LogTickFailed(Exception ex);
}

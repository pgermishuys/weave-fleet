using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Sessions;

/// <summary>
/// Once Fleet is back up after a restart, continues the sessions it cut off by itself for each user who turned that on
/// (<see cref="RestartContinuer.PreferenceKey"/>), the qualifying ones only (<see cref="RestartContinuer.Qualifies"/>).
/// Waits a moment first, so the notice with Stop all is on screen before the first message goes.
/// </summary>
public sealed partial class RestartContinueStartup(
    IServiceScopeFactory scopeFactory,
    RestartContinuer continuer,
    IHostApplicationLifetime lifetime,
    TimeProvider time,
    ILogger<RestartContinueStartup> logger) : BackgroundService
{
    /// <summary>How long after startup the first message waits.</summary>
    internal static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var started = new TaskCompletionSource();
            await using (lifetime.ApplicationStarted.Register(() => started.TrySetResult()))
                await started.Task.WaitAsync(stoppingToken).ConfigureAwait(false);

            List<(string UserId, List<string> SessionIds)> runs;
            using (var scope = scopeFactory.CreateScope())
            {
                var all = await scope.ServiceProvider.GetRequiredService<ISessionInterruptionRepository>().ListAllAsync().ConfigureAwait(false);
                runs = [];
                foreach (var byUser in all.GroupBy(item => item.UserId, StringComparer.Ordinal))
                {
                    var ids = await QualifyingAsync(byUser.Key, byUser.ToList(), stoppingToken).ConfigureAwait(false);
                    if (ids.Count > 0)
                        runs.Add((byUser.Key, ids));
                }
            }

            // Pending at once, so the notice says Fleet is continuing them and has Stop all through the grace period.
            foreach (var run in runs)
                continuer.Begin(run.UserId, run.SessionIds);
            await Task.Delay(GracePeriod, time, stoppingToken).ConfigureAwait(false);
            await Task.WhenAll(runs.Select(run => RunAsync(run.UserId, run.SessionIds, stoppingToken))).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LogFailed(ex);
        }
    }

    private async Task<List<string>> QualifyingAsync(string userId, List<SessionInterruption> cutOff, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        using var user = scope.ServiceProvider.GetRequiredService<IBackgroundUserScope>().Begin(userId);
        var preference = await scope.ServiceProvider.GetRequiredService<IUserPreferenceRepository>().GetAsync(RestartContinuer.PreferenceKey).ConfigureAwait(false);
        if (!string.Equals(preference, "true", StringComparison.OrdinalIgnoreCase)
            || !scope.ServiceProvider.GetRequiredService<SessionInterruptions>().Enabled)
            return [];

        var sessions = scope.ServiceProvider.GetRequiredService<ISessionRepository>();
        var registry = scope.ServiceProvider.GetRequiredService<IHarnessRegistry>();
        var ready = new Dictionary<string, bool>(StringComparer.Ordinal);
        var ids = new List<string>();
        foreach (var item in cutOff.OrderBy(item => item.At))
        {
            var session = await sessions.GetByIdAsync(item.SessionId).ConfigureAwait(false);
            if (session is null)
                continue;
            if (!ready.TryGetValue(session.HarnessType, out var isReady))
            {
                var runtime = registry.GetRuntimeByType(session.HarnessType);
                isReady = runtime is not null && (await runtime.CheckAvailabilityAsync(ct).ConfigureAwait(false)).Available;
                ready[session.HarnessType] = isReady;
            }

            if (RestartContinuer.Qualifies(item, session, time.GetUtcNow(), Directory.Exists(session.Directory), isReady))
                ids.Add(item.SessionId);
        }

        return ids;
    }

    private async Task RunAsync(string userId, List<string> ids, CancellationToken ct)
    {
        LogContinuing(userId, ids.Count);
        await continuer.RunAsync(ids, async (sessionId, token) =>
        {
            using var scope = scopeFactory.CreateScope();
            using var user = scope.ServiceProvider.GetRequiredService<IBackgroundUserScope>().Begin(userId);
            var result = await scope.ServiceProvider.GetRequiredService<SessionInterruptions>()
                .ContinueAsync(sessionId, automatic: true, token).ConfigureAwait(false);
            return result.IsSuccess;
        }, ct).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Restart: continuing {Count} cut-off sessions by itself for user {UserId}")]
    private partial void LogContinuing(string userId, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not continue the cut-off sessions by itself")]
    private partial void LogFailed(Exception ex);
}

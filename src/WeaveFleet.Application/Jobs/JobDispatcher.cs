using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Jobs;

/// <summary>
/// Hands waiting jobs to machines with a free seat and follows the jobs that run. One pass at a time: it reads how each
/// running job's session stands, then takes each waiting job, oldest first, to a machine that fits it. A seat is claimed
/// in the database before the session starts, so a job never starts twice; a start that fails puts the job back to
/// waiting with the reason.
/// </summary>
public sealed partial class JobDispatcher(IServiceScopeFactory scopeFactory, ILogger<JobDispatcher> logger) : IDisposable
{
    private readonly SemaphoreSlim _pass = new(1, 1);
    private readonly SemaphoreSlim _nudged = new(0, 1);

    public void Dispose()
    {
        _pass.Dispose();
        _nudged.Dispose();
    }

    /// <summary>Asks for a pass soon: a job was posted, or a machine's seats changed.</summary>
    public void Nudge()
    {
        try
        {
            _nudged.Release();
        }
        catch (SemaphoreFullException)
        {
            // One is already waiting.
        }
    }

    /// <summary>Waits until a pass is asked for, or <paramref name="interval"/> passes.</summary>
    internal Task<bool> WaitForNudgeAsync(TimeSpan interval, CancellationToken ct) => _nudged.WaitAsync(interval, ct);

    /// <summary>One pass, for every user who has a job that waits or runs and has jobs turned on.</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        await _pass.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            IReadOnlyList<string> users;
            using (var scope = scopeFactory.CreateScope())
                users = await scope.ServiceProvider.GetRequiredService<IJobRepository>().ListUsersWithOpenJobsAsync().ConfigureAwait(false);

            foreach (var userId in users)
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    using var user = scope.ServiceProvider.GetRequiredService<IBackgroundUserScope>().Begin(userId);
                    if (await scope.ServiceProvider.GetRequiredService<JobsFeature>().IsEnabledAsync().ConfigureAwait(false))
                        await PassAsync(scope.ServiceProvider, userId, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogPassFailed(ex, userId);
                }
            }
        }
        finally
        {
            _pass.Release();
        }
    }

    private async Task PassAsync(IServiceProvider services, string userId, CancellationToken ct)
    {
        var jobs = services.GetRequiredService<IJobRepository>();
        var sessions = services.GetRequiredService<IJobSessions>();

        foreach (var job in await jobs.ListAsync(userId).ConfigureAwait(false))
        {
            if (!JobStates.HoldsSeat(job.State))
                continue;

            // Its seat was claimed but Fleet stopped before the session started: nothing is running for it.
            if (job.SessionId is null)
            {
                await jobs.ReleaseAsync(job.Id, null).ConfigureAwait(false);
                continue;
            }

            await FollowAsync(jobs, job, await sessions.ReadAsync(job, ct).ConfigureAwait(false)).ConfigureAwait(false);
        }

        var all = await jobs.ListAsync(userId).ConfigureAwait(false);
        var waiting = all.Where(job => job.State == JobStates.Waiting).ToList();
        if (waiting.Count == 0)
            return;

        var cache = services.GetRequiredService<JobMachineCache>();
        var machines = await services.GetRequiredService<IJobMachines>().ListAsync(ct).ConfigureAwait(false);
        cache.Last = machines;

        var used = JobMatching.SeatsUsed(all);
        foreach (var job in waiting)
        {
            if (JobMatching.Pick(job, machines, used) is not { } pick)
                continue;

            var (machine, repository) = pick;
            if (!await jobs.ClaimAsync(job.Id, machine.Id, machine.Name).ConfigureAwait(false))
                continue;

            used[machine.Id] = used.GetValueOrDefault(machine.Id) + 1;
            var (sessionId, error) = await sessions.StartAsync(job, machine, repository, ct).ConfigureAwait(false);
            if (sessionId is null)
            {
                used[machine.Id]--;
                LogStartFailed(job.Id, machine.Name, error ?? "");
                await jobs.ReleaseAsync(job.Id, error).ConfigureAwait(false);
                continue;
            }

            await jobs.SetSessionAsync(job.Id, sessionId).ConfigureAwait(false);
        }
    }

    private static async Task FollowAsync(IJobRepository jobs, Job job, JobSessionState? state)
    {
        switch (state)
        {
            case JobSessionState.InTurn when job.State != JobStates.Running || !job.Worked:
                await jobs.SetStateAsync(job.Id, JobStates.Running, true, null).ConfigureAwait(false);
                break;
            case JobSessionState.WaitingInput when job.State != JobStates.NeedsYou || !job.Worked:
                await jobs.SetStateAsync(job.Id, JobStates.NeedsYou, true, null).ConfigureAwait(false);
                break;
            // A session reads idle before its first turn starts. Idle is only the end once it was seen working or has answered.
            case JobSessionState.Replied:
            case JobSessionState.Quiet when job.Worked:
                await jobs.SetStateAsync(job.Id, JobStates.Done, true, null).ConfigureAwait(false);
                break;
            case JobSessionState.Gone:
                await jobs.SetStateAsync(job.Id, JobStates.Failed, job.Worked, "Its session isn't there any more.").ConfigureAwait(false);
                break;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Jobs pass failed for user {UserId}")]
    private partial void LogPassFailed(Exception ex, string userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Job {JobId} didn't start on {Machine}: {Reason}")]
    private partial void LogStartFailed(string jobId, string machine, string reason);
}

/// <summary>Runs <see cref="JobDispatcher"/> when it's nudged and every <see cref="Interval"/> after, to catch what no one tells it: a session that ended, a machine that came back.</summary>
public sealed partial class JobDispatcherService(JobDispatcher dispatcher, ILogger<JobDispatcherService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await dispatcher.RunOnceAsync(stoppingToken).ConfigureAwait(false);
                await dispatcher.WaitForNudgeAsync(Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogRunFailed(ex);
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Jobs dispatcher pass failed")]
    private partial void LogRunFailed(Exception ex);
}

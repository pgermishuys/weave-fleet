using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Jobs;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Tests.Jobs;

/// <summary>With nothing to watch the dispatcher reads nothing: it waits for a nudge, not for a timer.</summary>
public sealed class JobDispatcherServiceTests
{
    [Fact]
    public void With_nothing_to_watch_it_waits_until_nudged_and_otherwise_every_few_seconds()
    {
        JobDispatcherService.NextWait(watching: false).ShouldBe(Timeout.InfiniteTimeSpan);
        JobDispatcherService.NextWait(watching: true).ShouldBe(JobDispatcherService.Interval);
    }

    [Fact]
    public async Task A_pass_with_no_open_jobs_has_nothing_to_watch()
    {
        var jobs = new NoOpenJobs();
        using var dispatcher = new JobDispatcher(TestServiceScopeFactory.Create(services => services.AddSingleton<IJobRepository>(jobs)), NullLogger<JobDispatcher>.Instance);

        (await dispatcher.RunOnceAsync(CancellationToken.None)).ShouldBeFalse();
        jobs.Reads.ShouldBe(1);
    }

    [Fact]
    public async Task A_nudge_ends_the_wait_with_no_timeout()
    {
        using var dispatcher = new JobDispatcher(TestServiceScopeFactory.CreateEmpty(), NullLogger<JobDispatcher>.Instance);
        var waiting = dispatcher.WaitForNudgeAsync(JobDispatcherService.NextWait(watching: false), CancellationToken.None);

        waiting.IsCompleted.ShouldBeFalse();
        dispatcher.Nudge();

        (await waiting).ShouldBeTrue();
    }

    private sealed class NoOpenJobs : IJobRepository
    {
        public int Reads { get; private set; }

        public Task<IReadOnlyList<string>> ListUsersWithOpenJobsAsync()
        {
            Reads++;
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        public Task InsertAsync(Job job) => throw new NotSupportedException();
        public Task<IReadOnlyList<Job>> ListAsync(string userId) => throw new NotSupportedException();
        public Task<bool> ClaimAsync(string id, string machineId, string machineName) => throw new NotSupportedException();
        public Task SetSessionAsync(string id, string sessionId) => throw new NotSupportedException();
        public Task ReleaseAsync(string id, string? reason) => throw new NotSupportedException();
        public Task SetStateAsync(string id, string state, bool worked, string? reason) => throw new NotSupportedException();
        public Task<bool> DeleteWaitingAsync(string id, string userId) => throw new NotSupportedException();
        public Task<IReadOnlyList<JobMachineSettings>> ListMachineSettingsAsync() => throw new NotSupportedException();
        public Task SaveMachineSettingsAsync(JobMachineSettings settings) => throw new NotSupportedException();
    }
}

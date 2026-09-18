using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Infrastructure.Tests.Harnesses;

public sealed class HarnessUpdateServiceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly FakeHarnessRuntime _runtime = new("opencode")
    {
        Availability = HarnessAvailability.Ready("1.18.20", "/home/you/.opencode/bin/opencode"),
        LatestVersionPackage = "opencode-ai",
        MinimumVersion = "1.15.10",
        UpdateCommand = (availability, version) => availability.ExecutablePath is null
            ? null
            : new HarnessCommand(availability.ExecutablePath, ["upgrade", version ?? ""], $"{availability.ExecutablePath} upgrade {version}"),
    };

    private readonly SessionActivityTracker _activity = new();
    private readonly List<string> _lookups = [];
    private readonly List<HarnessCommand> _ran = [];

    [Fact]
    public async Task Says_an_update_is_available_when_npm_has_a_newer_version()
    {
        var service = NewService();

        var info = (await service.DescribeAsync([await InfoAsync()], checkLatest: true, CancellationToken.None))["opencode"];

        info.LatestVersion.ShouldBe("1.18.31");
        info.UpdateAvailable.ShouldBeTrue();
        info.MinimumVersion.ShouldBe("1.15.10");
        info.Command.ShouldBe("/home/you/.opencode/bin/opencode upgrade 1.18.31");
        info.Job.ShouldBeNull();
    }

    [Fact]
    public async Task Looks_the_latest_version_up_once_an_hour_and_not_at_all_when_checks_are_off()
    {
        var service = NewService();
        var harness = await InfoAsync();

        await service.DescribeAsync([harness], checkLatest: false, CancellationToken.None);
        _lookups.ShouldBeEmpty();

        await service.DescribeAsync([harness], checkLatest: true, CancellationToken.None);
        await service.DescribeAsync([harness], checkLatest: true, CancellationToken.None);
        _lookups.ShouldBe(["opencode-ai"]);
    }

    [Fact]
    public async Task No_update_is_offered_when_the_install_is_current()
    {
        _runtime.Availability = HarnessAvailability.Ready("1.18.31", "/home/you/.opencode/bin/opencode");

        var info = (await NewService().DescribeAsync([await InfoAsync()], checkLatest: true, CancellationToken.None))["opencode"];

        info.UpdateAvailable.ShouldBeFalse();
    }

    [Fact]
    public async Task Waits_for_working_sessions_then_updates_and_checks_the_new_version()
    {
        _activity.Update("s1", ActivityStatuses.Busy, "local-user");
        _activity.Update("s2", ActivityStatuses.Busy, "local-user");
        var service = NewService(result: _ =>
        {
            _runtime.Availability = HarnessAvailability.Ready("1.18.31", "/home/you/.opencode/bin/opencode");
            return new HarnessUpdateService.UpdaterResult(0, "Installing opencode version: 1.18.31", TimedOut: false);
        });

        var started = await service.StartAsync("opencode", CancellationToken.None);

        started.Value!.Phase.ShouldBe(HarnessUpdatePhases.Waiting);
        (await JobAsync(service, job => job.WorkingSessions == 2)).Phase.ShouldBe(HarnessUpdatePhases.Waiting);
        _ran.ShouldBeEmpty();

        _activity.Update("s1", ActivityStatuses.Idle, "local-user");
        _activity.Update("s2", ActivityStatuses.WaitingInput, "local-user"); // waiting on a question isn't working

        var done = await JobAsync(service, job => job.Phase == HarnessUpdatePhases.Succeeded);
        done.Message.ShouldBe("Updated opencode from 1.18.20 to 1.18.31.");
        (done.FromVersion, done.ToVersion).ShouldBe(("1.18.20", "1.18.31"));
        done.Output.ShouldBe("Installing opencode version: 1.18.31");
        _ran.ShouldHaveSingleItem().Arguments.ShouldBe(["upgrade", "1.18.31"]);
        _runtime.AfterUpdateCalls.ShouldBe(1);
    }

    [Fact]
    public async Task A_failed_update_says_so_and_gives_the_command_to_run()
    {
        var service = NewService(result: _ => new HarnessUpdateService.UpdaterResult(1, "npm error code EACCES", TimedOut: false));

        await service.StartAsync("opencode", CancellationToken.None);
        var failed = await JobAsync(service, job => job.Phase == HarnessUpdatePhases.Failed);

        failed.Message.ShouldBe("The update failed (exit code 1). opencode is still on 1.18.20. Run it yourself: /home/you/.opencode/bin/opencode upgrade 1.18.31");
        failed.Output.ShouldBe("npm error code EACCES");
        _runtime.AfterUpdateCalls.ShouldBe(0);
    }

    [Fact]
    public async Task An_update_that_leaves_the_version_unchanged_is_a_failure()
    {
        var service = NewService(result: _ => new HarnessUpdateService.UpdaterResult(0, "Claude is up to date!", TimedOut: false));

        await service.StartAsync("opencode", CancellationToken.None);
        var failed = await JobAsync(service, job => job.Phase == HarnessUpdatePhases.Failed);

        failed.Message.ShouldStartWith("The update finished, but opencode is still on 1.18.20.");
    }

    [Fact]
    public async Task A_waiting_update_can_be_cancelled_and_never_runs()
    {
        _activity.Update("s1", ActivityStatuses.Busy, "local-user");
        var service = NewService();
        await service.StartAsync("opencode", CancellationToken.None);

        service.Dismiss("opencode").IsSuccess.ShouldBeTrue();
        _activity.Update("s1", ActivityStatuses.Idle, "local-user");
        await Task.Delay(200);

        _ran.ShouldBeEmpty();
        (await service.DescribeAsync([await InfoAsync()], checkLatest: false, CancellationToken.None))["opencode"].Job.ShouldBeNull();
    }

    [Fact]
    public async Task One_update_at_a_time_and_a_running_one_cannot_be_stopped()
    {
        var release = new TaskCompletionSource();
        var service = NewService(resultAsync: async _ =>
        {
            await release.Task;
            return new HarnessUpdateService.UpdaterResult(1, "", TimedOut: false);
        });

        await service.StartAsync("opencode", CancellationToken.None);
        await JobAsync(service, job => job.Phase == HarnessUpdatePhases.Running);

        (await service.StartAsync("opencode", CancellationToken.None)).Error!.Code.ShouldBe("Harness.Conflict");
        service.Dismiss("opencode").Error!.Code.ShouldBe("Harness.Conflict");

        release.SetResult();
        await JobAsync(service, job => job.Phase == HarnessUpdatePhases.Failed);
        (await service.StartAsync("opencode", CancellationToken.None)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task A_harness_Fleet_cannot_update_is_refused()
    {
        _runtime.UpdateCommand = (_, _) => null;

        var started = await NewService().StartAsync("opencode", CancellationToken.None);

        started.Error!.Description.ShouldBe("Fleet can't update opencode. Update it the way you installed it.");
    }

    private HarnessUpdateService NewService(
        Func<HarnessCommand, HarnessUpdateService.UpdaterResult>? result = null,
        Func<HarnessCommand, Task<HarnessUpdateService.UpdaterResult>>? resultAsync = null)
    {
        var registry = new FakeHarnessRegistry();
        registry.Register(new FakeHarness("opencode", "opencode"));
        registry.Register(_runtime);
        return new HarnessUpdateService(registry, _activity, new NoHttpClientFactory(), NullLogger<HarnessUpdateService>.Instance)
        {
            WaitPoll = TimeSpan.FromMilliseconds(20),
            FetchLatest = (package, _) =>
            {
                lock (_lookups) _lookups.Add(package);
                return Task.FromResult<string?>("1.18.31");
            },
            RunUpdater = async (command, _, _) =>
            {
                lock (_ran) _ran.Add(command);
                if (resultAsync is not null) return await resultAsync(command);
                return (result ?? (_ => new HarnessUpdateService.UpdaterResult(0, "", TimedOut: false)))(command);
            },
        };
    }

    private async Task<HarnessInfo> InfoAsync() =>
        HarnessInfo.From("opencode", "opencode", new HarnessCapabilities(), await _runtime.CheckAvailabilityAsync(CancellationToken.None));

    private async Task<HarnessUpdateJob> JobAsync(HarnessUpdateService service, Func<HarnessUpdateJob, bool> until)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        while (true)
        {
            var job = (await service.DescribeAsync([await InfoAsync()], checkLatest: false, CancellationToken.None))["opencode"].Job;
            if (job is not null && until(job)) return job;
            await Task.Delay(20, timeout.Token);
        }
    }

    private sealed class NoHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("Tests don't reach npm.");
    }
}

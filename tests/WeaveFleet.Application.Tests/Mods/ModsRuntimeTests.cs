using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Mods;
using WeaveFleet.Application.Runtimes;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Mods;

public sealed class ModsRuntimeTests
{
    private const string Alice = "alice";

    private readonly FakeBun _bun = new();
    private readonly InMemoryUserPreferenceRepository _preferences = new();
    private readonly RecordingScope _scope = new();
    private readonly FakeEventBroadcaster _events = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 10, 9, 0, 0, TimeSpan.Zero));
    private readonly BunRelease _release = new("1.4.2", [new BunAsset(BunRelease.CurrentRid(), "bun.zip", new string('a', 64)) { Size = 36_646_949 }]);

    private ModsRuntime NewRuntime() => new(
        _bun, new FixedReleases(_release), _events, TestServiceScopeFactory.Create(services => services.AddScoped<IUserPreferenceRepository>(_ => _preferences)), _scope, new FleetOptions(), _clock, NullLogger<ModsRuntime>.Instance)
    {
        Home = "/home/alice",
    };

    private IEnumerable<ModsRuntimePayload> Sent() =>
        _events.Broadcasts.Where(b => b.Type == "mods.runtime").Select(b => ((ModsRuntimeChanged)b.DomainEvent!).Payload);

    private static BunInstallJob Job(string phase, long received) => new(phase, "1.4.2", "Installing…", received, 36_646_949);

    [Fact]
    public async Task A_first_install_sends_each_phase_to_everyone_and_the_view_shows_the_result()
    {
        _bun.Script = report =>
        {
            report(Job("downloading", 0));
            report(Job("verifying", 100));
            report(Job("succeeded", 100));
        };
        var runtime = NewRuntime();

        (await runtime.StartInstallAsync(Alice, CancellationToken.None)).ShouldBeTrue();
        await runtime.WhenIdle;
        await runtime.Sent;

        Sent().Select(p => (p.Job!.Phase, p.Reason)).ShouldBe([("downloading", "job"), ("verifying", "job"), ("succeeded", "installed")]);
        _events.Broadcasts.ShouldAllBe(b => b.UserId == null && b.Topic == "sessions");
        var view = await runtime.GetViewAsync(CancellationToken.None);
        view.Release.ShouldBe(new ModsRuntimeRelease("1.4.2", 36_646_949, true, "~/.weave/runtimes/bun/1.4.2", "github.com/oven-sh/bun"));
        view.Bun!.Version.ShouldBe("1.4.2");
        view.Job!.Phase.ShouldBe("succeeded");
    }

    [Fact]
    public async Task Progress_is_sent_at_most_every_250_ms_but_phases_and_the_end_always_are()
    {
        var gate = _bun.Hold();
        var runtime = NewRuntime();
        await runtime.StartInstallAsync(Alice, CancellationToken.None);
        await gate.Started.Task;

        gate.Report(Job("downloading", 1000));
        gate.Report(Job("downloading", 2000));
        _clock.Advance(TimeSpan.FromMilliseconds(249));
        gate.Report(Job("downloading", 3000));
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        gate.Report(Job("downloading", 4000));
        gate.Report(Job("succeeded", 4000));
        gate.Release();
        await runtime.WhenIdle;
        await runtime.Sent;

        Sent().Select(p => (p.Job!.Phase, p.Job.BytesReceived)).ShouldBe([("downloading", 1000L), ("downloading", 4000L), ("succeeded", 4000L)]);
    }

    [Fact]
    public async Task A_second_request_joins_the_install_that_is_running()
    {
        var gate = _bun.Hold();
        var runtime = NewRuntime();
        await runtime.StartInstallAsync(Alice, CancellationToken.None);
        await gate.Started.Task;

        (await runtime.StartInstallAsync("bob", CancellationToken.None)).ShouldBeTrue();
        gate.Release();
        await runtime.WhenIdle;

        _bun.Ensured.ShouldBe(1);
    }

    [Fact]
    public async Task A_failed_first_install_turns_the_switch_off_and_says_why()
    {
        _preferences.Seed("Mods", "true");
        _bun.Fail = (BunInstallFailures.Offline, "Fleet couldn't reach github.com: the connection timed out.");
        var runtime = NewRuntime();

        await runtime.StartInstallAsync(Alice, CancellationToken.None);
        await runtime.WhenIdle;
        await runtime.Sent;

        (await _preferences.GetAsync("Mods")).ShouldBe("false");
        _scope.Users.ShouldBe([Alice]);
        var changed = _events.Broadcasts.Single(b => b.Type == "mods.changed");
        changed.UserId.ShouldBe(Alice);
        ((ModsChanged)changed.DomainEvent!).Payload.Reason.ShouldBe("switch");
        Sent().Last().Job.ShouldNotBeNull().Reason.ShouldBe("offline");
    }

    [Fact]
    public async Task A_failure_that_leaves_a_usable_bun_keeps_the_switch()
    {
        _preferences.Seed("Mods", "true");
        _bun.Install("1.4.0");
        _bun.Fail = (BunInstallFailures.Stopped, "The download stopped.");
        var runtime = NewRuntime();

        await runtime.StartInstallAsync(Alice, CancellationToken.None);
        await runtime.WhenIdle;

        (await _preferences.GetAsync("Mods")).ShouldBe("true");
    }

    [Fact]
    public async Task Cancelling_ends_the_install_as_cancelled_and_turns_the_switch_off()
    {
        _preferences.Seed("Mods", "true");
        var gate = _bun.Hold(waitForCancel: true);
        var runtime = NewRuntime();
        await runtime.StartInstallAsync(Alice, CancellationToken.None);
        await gate.Started.Task;

        (await runtime.CancelAsync(CancellationToken.None)).ShouldBeTrue();
        await runtime.Sent;

        Sent().Last().Job!.Reason.ShouldBe("cancelled");
        (await _preferences.GetAsync("Mods")).ShouldBe("false");
        _scope.Users.ShouldBe([Alice]);
        (await runtime.CancelAsync(CancellationToken.None)).ShouldBeFalse();
    }

    private sealed class FixedReleases(BunRelease current) : IBunReleases
    {
        public BunRelease Current { get; } = current;
    }

    /// <summary>Plays the installer: scripted reports, a gate to pause on, and a configurable failure.</summary>
    private sealed class FakeBun : IBunRuntime
    {
        private readonly List<BunLocation> _installed = [];
        private Gate? _gate;

        public BunInstallJob? Job => null;
        public int Ensured { get; private set; }
        public Action<Action<BunInstallJob>>? Script { get; set; }
        public (string Reason, string Message)? Fail { get; set; }

        public void Install(string version) => _installed.Add(new BunLocation($"/bun/{version}/bun", BunSources.Installed, version));

        public Gate Hold(bool waitForCancel = false) => _gate = new Gate(waitForCancel);

        public Task<BunLocation?> FindAsync(BunRelease release, CancellationToken ct) => Task.FromResult(_installed.LastOrDefault());

        public async Task<Result<BunLocation>> EnsureAsync(BunRelease release, IProgress<BunInstallJob>? progress, CancellationToken ct)
        {
            Ensured++;
            void Report(BunInstallJob job) => progress?.Report(job);

            if (_gate is { } gate)
            {
                gate.Reporter = Report;
                gate.Started.TrySetResult();
                try
                {
                    await (gate.WaitForCancel ? new TaskCompletionSource().Task : gate.Released.Task).WaitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    Report(new BunInstallJob(BunInstallPhases.Failed, release.Version, "The install was cancelled.", 0, null, BunInstallFailures.Cancelled));
                    throw;
                }
            }

            Script?.Invoke(Report);
            if (Fail is { } failure)
            {
                Report(new BunInstallJob(BunInstallPhases.Failed, release.Version, failure.Message, 0, null, failure.Reason));
                return new FleetError("Mods.Runtime", failure.Message);
            }

            Install(release.Version);
            return _installed[^1];
        }

        public Task<IReadOnlyList<BunCandidate>> FindOnMachineAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<BunCandidate> CheckAsync(string path, CancellationToken ct) => throw new NotSupportedException();
        public IReadOnlyList<BunLocation> Installed() => _installed;
        public Task<IReadOnlyList<string>> PruneAsync(IReadOnlyCollection<string> inUse, CancellationToken ct) => throw new NotSupportedException();
        public BunSafety SafetyOf(BunLocation location, BunRelease release) => new(true, false, null);
    }

    private sealed class Gate(bool waitForCancel)
    {
        public bool WaitForCancel { get; } = waitForCancel;
        public Action<BunInstallJob>? Reporter { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => Released.TrySetResult();
        public void Report(BunInstallJob job) => Reporter!(job);
    }

    /// <summary>Records the users Fleet acted for; the preference repository is shared.</summary>
    private sealed class RecordingScope : IBackgroundUserScope
    {
        public List<string> Users { get; } = [];

        public IDisposable Begin(string userId)
        {
            Users.Add(userId);
            return new Nothing();
        }

        private sealed class Nothing : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}

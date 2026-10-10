using System.Text.Json;
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

namespace WeaveFleet.Application.Tests.Mods;

public sealed class ModsRuntimeTests : IDisposable
{
    private const string Alice = "alice";

    private readonly string _home = Path.Combine(Path.GetTempPath(), $"fleet-modsrt-{Guid.NewGuid():N}");
    private readonly FakeBunRuntime _bun = new();
    private readonly FakeBunReleases _releases = new(Release("1.4.2"));
    private readonly FakeBunPathSetting _setting = new();
    private readonly FakeEventBroadcaster _events = new();
    private readonly FakePreferences _preferences = new();
    private readonly ModsSafeMode _safeMode = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 10, 9, 0, 0, TimeSpan.Zero));
    private readonly FleetOptions _options = new();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_home, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private static BunRelease Release(string version, string oldestSafe = "1.4.0") =>
        new(version, [new BunAsset(BunRelease.CurrentRid(), "bun.zip", new string('a', 64)) { Size = 36_646_949 }]) { OldestSafe = oldestSafe };

    private ModsRuntime NewRuntime() => new(
        _bun, _releases, _setting, _preferences, _safeMode, _events,
        _preferences.Scopes(), _preferences, _options, _clock, NullLogger<ModsRuntime>.Instance)
    {
        Home = _home,
    };

    private IEnumerable<FakeEventBroadcaster.BroadcastRecord> RuntimeEvents() => _events.Broadcasts.Where(b => b.Type == "mods.runtime");

    private async Task<int> CountAsync(ModsRuntime runtime)
    {
        await runtime.Sent;
        return RuntimeEvents().Count();
    }

    private static ModsRuntimePayload PayloadOf(FakeEventBroadcaster.BroadcastRecord record) => ((ModsRuntimeChanged)record.DomainEvent!).Payload;

    // -- kinds -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_first_install_is_an_install()
    {
        var runtime = NewRuntime();

        (await runtime.StartInstallAsync(null, Alice, CancellationToken.None)).ShouldBeTrue();
        await runtime.WhenIdle;

        _bun.Ensured.ShouldBe(["1.4.2"]);
        runtime.Job!.Kind.ShouldBe("install");
        runtime.Job.Phase.ShouldBe("succeeded");
        runtime.Job.From.ShouldBeNull();
        runtime.Job.Version.ShouldBe("1.4.2");
        runtime.Job.StartedAt.ShouldBe(_clock.GetUtcNow());
    }

    [Fact]
    public async Task A_newer_release_over_a_safe_bun_is_an_update_and_remembers_what_mods_run_on()
    {
        _releases.Current = Release("1.4.3");
        _bun.Install("1.4.2");
        var runtime = NewRuntime();

        await runtime.StartInstallAsync(null, null, CancellationToken.None);
        await runtime.WhenIdle;

        runtime.Job!.Kind.ShouldBe("update");
        runtime.Job.From.ShouldBe("1.4.2");
    }

    [Fact]
    public async Task A_newer_release_over_an_unsafe_bun_is_a_security_install()
    {
        _releases.Current = Release("1.4.3", oldestSafe: "1.4.2");
        _bun.Install("1.4.1");
        var runtime = NewRuntime();

        await runtime.StartInstallAsync(null, null, CancellationToken.None);
        await runtime.WhenIdle;

        runtime.Job!.Kind.ShouldBe("security");
        runtime.Job.From.ShouldBe("1.4.1");
    }

    [Theory]
    [InlineData("install")]
    [InlineData("update")]
    [InlineData("security")]
    public async Task A_kind_that_is_given_is_the_kind_that_is_used(string kind)
    {
        var runtime = NewRuntime();

        await runtime.StartInstallAsync(kind, null, CancellationToken.None);
        await runtime.WhenIdle;

        runtime.Job!.Kind.ShouldBe(kind);
    }

    [Fact]
    public async Task Nothing_installs_when_the_releases_bun_is_already_there()
    {
        _bun.Install("1.4.2");
        var runtime = NewRuntime();

        (await runtime.StartInstallAsync(null, Alice, CancellationToken.None)).ShouldBeFalse();

        _bun.Ensured.ShouldBeEmpty();
        runtime.Job.ShouldBeNull();
    }

    [Fact]
    public async Task A_second_request_joins_the_install_that_is_running()
    {
        var gate = _bun.Hold();
        var runtime = NewRuntime();

        (await runtime.StartInstallAsync(null, Alice, CancellationToken.None)).ShouldBeTrue();
        await gate.Started.WaitAsync(TimeSpan.FromSeconds(10));
        (await runtime.StartInstallAsync(null, "bob", CancellationToken.None)).ShouldBeTrue();
        gate.Release();
        await runtime.WhenIdle;

        _bun.Ensured.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Installing_runs_apart_from_the_request_that_started_it()
    {
        var gate = _bun.Hold();
        var runtime = NewRuntime();
        using var request = new CancellationTokenSource();

        await runtime.StartInstallAsync(null, Alice, request.Token);
        await gate.Started.WaitAsync(TimeSpan.FromSeconds(10));
        await request.CancelAsync();
        gate.Release();
        await runtime.WhenIdle;

        runtime.Job!.Phase.ShouldBe("succeeded");
    }

    // -- events ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Every_phase_change_is_sent_at_once_to_everyone_and_the_last_one_says_installed()
    {
        _bun.Script = report =>
        {
            report(Job("downloading", 0));
            report(Job("verifying", 100));
            report(Job("extracting", 100));
            report(Job("succeeded", 100, "Installed Bun 1.4.2."));
        };
        var runtime = NewRuntime();

        await runtime.StartInstallAsync(null, Alice, CancellationToken.None);
        await runtime.WhenIdle;

        var sent = RuntimeEvents().ToList();
        sent.Select(e => PayloadOf(e).Job!.Phase).ShouldBe(["downloading", "verifying", "extracting", "succeeded"]);
        sent.Select(e => PayloadOf(e).Reason).ShouldBe(["job", "job", "job", "installed"]);
        sent.ShouldAllBe(e => e.Topic == "sessions" && e.UserId == null);
        sent[0].Payload.GetProperty("reason").GetString().ShouldBe("job");
        sent[0].Payload.GetProperty("job").GetProperty("kind").GetString().ShouldBe("install");
    }

    [Fact]
    public async Task Progress_inside_the_interval_is_sent_once_but_a_phase_change_and_the_end_always_are()
    {
        var gate = _bun.Hold(report =>
        {
            report(Job("downloading", 0));
            for (var i = 1; i <= 4; i++)
            {
                _clock.Advance(TimeSpan.FromMilliseconds(40));
                report(Job("downloading", i * 1000));
            }
        });
        var runtime = NewRuntime();

        await runtime.StartInstallAsync(null, Alice, CancellationToken.None);
        await gate.Started.WaitAsync(TimeSpan.FromSeconds(10));
        (await CountAsync(runtime)).ShouldBe(1);

        gate.Report(Job("downloading", 5000));
        (await CountAsync(runtime)).ShouldBe(1);

        _clock.Advance(TimeSpan.FromMilliseconds(250));
        gate.Report(Job("downloading", 6000));
        (await CountAsync(runtime)).ShouldBe(2);
        PayloadOf(RuntimeEvents().Last()).Job!.BytesReceived.ShouldBe(6000);

        gate.Report(Job("verifying", 6000));
        (await CountAsync(runtime)).ShouldBe(3);
        gate.Report(Job("succeeded", 6000, "Installed Bun 1.4.2."));
        gate.Release();
        await runtime.WhenIdle;

        RuntimeEvents().Last().Payload.GetProperty("reason").GetString().ShouldBe("installed");
        RuntimeEvents().Count().ShouldBe(4);
    }

    [Fact]
    public async Task A_failure_is_sent_with_its_reason()
    {
        _bun.Fail = (BunInstallFailures.Offline, "Fleet couldn't reach github.com: the connection timed out. Check that this computer is online, then try again.");
        var runtime = NewRuntime();

        await runtime.StartInstallAsync(null, null, CancellationToken.None);
        await runtime.WhenIdle;

        var last = PayloadOf(RuntimeEvents().Last());
        last.Job!.Phase.ShouldBe("failed");
        last.Job.Reason.ShouldBe("offline");
        last.Job.Message.ShouldStartWith("Fleet couldn't reach github.com");
        last.Reason.ShouldBe("job");
        runtime.Job!.Reason.ShouldBe("offline");
    }

    // -- the switch ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_first_install_that_fails_turns_the_switch_off_for_the_user_who_started_it()
    {
        _preferences.Seed(Alice, "Mods", "true");
        _bun.Fail = (BunInstallFailures.Blocked, "github.com answered 403 Forbidden, so a proxy or firewall is probably blocking downloads from GitHub.");
        var runtime = NewRuntime();

        await runtime.StartInstallAsync(null, Alice, CancellationToken.None);
        await runtime.WhenIdle;

        _preferences.Get(Alice, "Mods").ShouldBe("false");
        var changed = _events.Broadcasts.Single(b => b.Type == "mods.changed");
        changed.UserId.ShouldBe(Alice);
        ((ModsChanged)changed.DomainEvent!).Payload.Reason.ShouldBe("switch");
    }

    [Fact]
    public async Task Every_user_who_asked_for_the_failed_install_is_switched_off()
    {
        _preferences.Seed(Alice, "Mods", "true");
        _preferences.Seed("bob", "Mods", "true");
        var gate = _bun.Hold();
        _bun.Fail = (BunInstallFailures.Offline, "Fleet couldn't reach github.com: the connection timed out. Check that this computer is online, then try again.");
        var runtime = NewRuntime();

        await runtime.StartInstallAsync(null, Alice, CancellationToken.None);
        await gate.Started.WaitAsync(TimeSpan.FromSeconds(10));
        await runtime.StartInstallAsync(null, "bob", CancellationToken.None);
        gate.Release();
        await runtime.WhenIdle;

        _preferences.Get(Alice, "Mods").ShouldBe("false");
        _preferences.Get("bob", "Mods").ShouldBe("false");
    }

    [Fact]
    public async Task A_failed_install_that_fleet_started_itself_never_touches_a_switch()
    {
        _preferences.Seed(Alice, "Mods", "true");
        _bun.Fail = (BunInstallFailures.Offline, "Fleet couldn't reach github.com: the connection timed out. Check that this computer is online, then try again.");
        var runtime = NewRuntime();

        await runtime.StartInstallAsync(null, null, CancellationToken.None);
        await runtime.WhenIdle;

        _preferences.Get(Alice, "Mods").ShouldBe("true");
        _events.Broadcasts.ShouldNotContain(b => b.Type == "mods.changed");
    }

    [Fact]
    public async Task A_failed_update_keeps_the_switch_on_because_mods_keep_running()
    {
        _releases.Current = Release("1.4.3");
        _bun.Install("1.4.2");
        _preferences.Seed(Alice, "Mods", "true");
        _bun.Fail = (BunInstallFailures.Offline, "Fleet couldn't reach github.com: the connection timed out. Check that this computer is online, then try again.");
        var runtime = NewRuntime();

        await runtime.StartInstallAsync(null, Alice, CancellationToken.None);
        await runtime.WhenIdle;

        _preferences.Get(Alice, "Mods").ShouldBe("true");
    }

    [Fact]
    public async Task A_failed_install_leaves_the_switch_when_a_bun_is_usable_after_all()
    {
        _preferences.Seed(Alice, "Mods", "true");
        _bun.Fail = (BunInstallFailures.Stopped, "The download stopped after 21 of 35 MB. Try again.");
        _bun.UserBun[Alice] = new BunLocation("/opt/bun", BunSources.Configured, "1.4.5");
        var runtime = NewRuntime();

        await runtime.StartInstallAsync("install", Alice, CancellationToken.None);
        await runtime.WhenIdle;

        _preferences.Get(Alice, "Mods").ShouldBe("true");
    }

    [Fact]
    public async Task Cancelling_ends_the_install_and_turns_the_switch_off_for_a_first_install()
    {
        _preferences.Seed(Alice, "Mods", "true");
        var gate = _bun.Hold(waitForCancel: true);
        var runtime = NewRuntime();
        await runtime.StartInstallAsync(null, Alice, CancellationToken.None);
        await gate.Started.WaitAsync(TimeSpan.FromSeconds(10));

        (await runtime.CancelAsync(CancellationToken.None)).ShouldBeTrue();

        runtime.Job!.Phase.ShouldBe("failed");
        runtime.Job.Reason.ShouldBe("cancelled");
        _preferences.Get(Alice, "Mods").ShouldBe("false");
        PayloadOf(RuntimeEvents().Last()).Job!.Reason.ShouldBe("cancelled");
    }

    [Fact]
    public async Task Cancelling_with_nothing_running_says_so()
    {
        (await NewRuntime().CancelAsync(CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_new_install_can_start_after_one_ends()
    {
        _bun.Fail = (BunInstallFailures.Offline, "Fleet couldn't reach github.com: the connection timed out. Check that this computer is online, then try again.");
        var runtime = NewRuntime();
        await runtime.StartInstallAsync(null, null, CancellationToken.None);
        await runtime.WhenIdle;

        _bun.Fail = null;
        await runtime.StartInstallAsync(null, null, CancellationToken.None);
        await runtime.WhenIdle;

        runtime.Job!.Phase.ShouldBe("succeeded");
        _bun.Ensured.Count.ShouldBe(2);
    }

    // -- the release manifest --------------------------------------------------------------------------------------

    [Fact]
    public async Task A_user_turning_mods_on_installs_the_release_the_manifest_has_now()
    {
        _releases.OnRefresh = () => _releases.Current = Release("1.4.5");
        var runtime = NewRuntime();

        await runtime.StartInstallAsync(null, Alice, CancellationToken.None);
        await runtime.WhenIdle;

        _bun.Ensured.ShouldBe(["1.4.5"]);
    }

    [Fact]
    public async Task A_manifest_that_fails_still_installs_the_release_fleet_has()
    {
        _releases.OnRefresh = () => throw new InvalidOperationException("no network");
        var runtime = NewRuntime();

        (await runtime.StartInstallAsync(null, Alice, CancellationToken.None)).ShouldBeTrue();
        await runtime.WhenIdle;

        _bun.Ensured.ShouldBe(["1.4.2"]);
    }

    [Fact]
    public async Task A_manifest_that_hangs_is_given_up_on_after_the_cap()
    {
        var hang = new TaskCompletionSource();
        _releases.RefreshTask = hang.Task;
        var runtime = NewRuntime();

        var start = runtime.StartInstallAsync(null, Alice, CancellationToken.None);
        start.IsCompleted.ShouldBeFalse();
        _clock.Advance(TimeSpan.FromSeconds(3));
        (await start.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBeTrue();
        await runtime.WhenIdle;

        _bun.Ensured.ShouldBe(["1.4.2"]);
        _releases.Refreshes.ShouldBe(1);
    }

    [Fact]
    public async Task An_install_fleet_starts_itself_does_not_ask_for_the_manifest()
    {
        var runtime = NewRuntime();

        await runtime.StartInstallAsync(null, null, CancellationToken.None);
        await runtime.WhenIdle;

        _releases.Refreshes.ShouldBe(0);
    }

    // -- the signal for the host -----------------------------------------------------------------------------------

    [Fact]
    public async Task A_finished_install_raises_BunChanged_for_everyone()
    {
        var runtime = NewRuntime();
        var raised = new List<string?>();
        runtime.BunChanged += (_, e) => raised.Add(e.UserId);

        await runtime.StartInstallAsync(null, Alice, CancellationToken.None);
        await runtime.WhenIdle;

        raised.ShouldBe([null]);
    }

    [Fact]
    public async Task A_failed_install_does_not_raise_BunChanged()
    {
        _bun.Fail = (BunInstallFailures.Offline, "Fleet couldn't reach github.com: the connection timed out. Check that this computer is online, then try again.");
        var runtime = NewRuntime();
        var raised = 0;
        runtime.BunChanged += (_, _) => raised++;

        await runtime.StartInstallAsync(null, null, CancellationToken.None);
        await runtime.WhenIdle;

        raised.ShouldBe(0);
    }

    [Fact]
    public async Task Saving_and_clearing_a_users_own_bun_raises_BunChanged_for_that_user_and_tells_only_them()
    {
        var runtime = NewRuntime();
        var raised = new List<string?>();
        runtime.BunChanged += (_, e) => raised.Add(e.UserId);

        await runtime.SaveBunPathAsync(Alice, "/opt/tools/bun/bin/bun", CancellationToken.None);
        await runtime.SaveBunPathAsync(Alice, null, CancellationToken.None);
        await runtime.WhenIdle;

        raised.ShouldBe([Alice, Alice]);
        _setting.Saves.ShouldBe([(Alice, "/opt/tools/bun/bin/bun"), (Alice, null)]);
        await runtime.Sent;
        var sent = RuntimeEvents().ToList();
        sent.Count.ShouldBe(2);
        sent.ShouldAllBe(e => e.UserId == Alice && PayloadOf(e).Reason == "bun-path");
    }

    [Fact]
    public async Task A_handler_that_throws_does_not_stop_the_install_ending()
    {
        var runtime = NewRuntime();
        runtime.BunChanged += (_, _) => throw new InvalidOperationException("boom");

        await runtime.StartInstallAsync(null, Alice, CancellationToken.None);
        await runtime.WhenIdle;

        runtime.Job!.Phase.ShouldBe("succeeded");
    }

    // -- what Fleet installs by itself -----------------------------------------------------------------------------

    [Fact]
    public async Task Start_up_with_mods_on_and_no_bun_installs()
    {
        _preferences.Seed(Alice, "Mods", "true");
        var runtime = NewRuntime();

        await runtime.EvaluateAsync(CancellationToken.None);
        await runtime.WhenIdle;

        _bun.Ensured.ShouldBe(["1.4.2"]);
        runtime.Job!.Kind.ShouldBe("install");
    }

    [Fact]
    public async Task The_option_turns_mods_on_for_the_local_user_who_has_no_stored_choice()
    {
        _options.Harness.Mods = true;
        var runtime = NewRuntime();

        await runtime.EvaluateAsync(CancellationToken.None);
        await runtime.WhenIdle;

        _bun.Ensured.ShouldBe(["1.4.2"]);
    }

    [Fact]
    public async Task A_stored_off_beats_the_option()
    {
        _options.Harness.Mods = true;
        _preferences.Seed("local-user", "Mods", "false");
        var runtime = NewRuntime();

        await runtime.EvaluateAsync(CancellationToken.None);

        _bun.Ensured.ShouldBeEmpty();
    }

    [Fact]
    public async Task Nothing_installs_by_itself_when_nobody_has_mods_on()
    {
        _preferences.Seed(Alice, "Mods", "false");
        var runtime = NewRuntime();

        await runtime.EvaluateAsync(CancellationToken.None);

        _bun.Ensured.ShouldBeEmpty();
    }

    [Fact]
    public async Task Nothing_installs_by_itself_for_a_user_in_safe_mode()
    {
        _preferences.Seed(Alice, "Mods", "true");
        _safeMode.Set(Alice, true);
        var runtime = NewRuntime();

        await runtime.EvaluateAsync(CancellationToken.None);

        _bun.Ensured.ShouldBeEmpty();
    }

    [Fact]
    public async Task Another_user_without_safe_mode_still_wants_it()
    {
        _preferences.Seed(Alice, "Mods", "true");
        _preferences.Seed("bob", "Mods", "true");
        _safeMode.Set(Alice, true);
        var runtime = NewRuntime();

        await runtime.EvaluateAsync(CancellationToken.None);
        await runtime.WhenIdle;

        _bun.Ensured.ShouldBe(["1.4.2"]);
    }

    [Fact]
    public async Task Nothing_installs_by_itself_for_a_user_with_their_own_bun()
    {
        _preferences.Seed(Alice, "Mods", "true");
        _preferences.Seed(Alice, "ModsBunPath", "/opt/tools/bun/bin/bun");
        var runtime = NewRuntime();

        await runtime.EvaluateAsync(CancellationToken.None);

        _bun.Ensured.ShouldBeEmpty();
    }

    [Fact]
    public async Task Nothing_installs_by_itself_when_configuration_sets_a_bun()
    {
        _preferences.Seed(Alice, "Mods", "true");
        _setting.FromConfiguration = "/etc/fleet/bun";
        var runtime = NewRuntime();

        await runtime.EvaluateAsync(CancellationToken.None);

        _bun.Ensured.ShouldBeEmpty();
    }

    [Fact]
    public async Task Nothing_installs_by_itself_when_the_release_is_already_installed()
    {
        _preferences.Seed(Alice, "Mods", "true");
        _bun.Install("1.4.2");
        var runtime = NewRuntime();

        await runtime.EvaluateAsync(CancellationToken.None);

        _bun.Ensured.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_new_release_installs_by_itself_as_an_update_and_clients_are_told()
    {
        _preferences.Seed(Alice, "Mods", "true");
        _bun.Install("1.4.2");
        _releases.Current = Release("1.4.3");
        var runtime = NewRuntime();

        await runtime.ReleaseChangedAsync(CancellationToken.None);
        await runtime.WhenIdle;

        runtime.Job!.Kind.ShouldBe("update");
        PayloadOf(RuntimeEvents().First()).Reason.ShouldBe("release");
    }

    [Fact]
    public async Task The_hosted_service_checks_after_the_start_delay_on_a_new_release_and_on_the_interval()
    {
        _preferences.Seed(Alice, "Mods", "true");
        var runtime = NewRuntime();
        using var service = new ModsRuntimeHostedService(runtime, _releases, _options, NullLogger<ModsRuntimeHostedService>.Instance)
        {
            StartDelay = TimeSpan.Zero,
            Interval = TimeSpan.FromMilliseconds(100),
        };

        await service.StartAsync(CancellationToken.None);
        await WaitForAsync(() => runtime.Job is { Phase: "succeeded" });
        _bun.Ensured.ShouldBe(["1.4.2"]);

        // A new release comes, and the interval's own tick finds the same thing: one install for it.
        _releases.Current = Release("1.4.3");
        _releases.RaiseChanged(Release("1.4.2"));
        await WaitForAsync(() => runtime.Job is { Version: "1.4.3", Phase: "succeeded" });
        await service.StopAsync(CancellationToken.None);

        _bun.Ensured.ShouldBe(["1.4.2", "1.4.3"]);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
                throw new TimeoutException("The condition never came true.");
            await Task.Delay(10);
        }
    }

    // -- the view --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_view_describes_the_bun_the_release_and_what_is_installed()
    {
        var folder = Path.Combine(_home, ".weave", "runtimes", "bun", "1.4.2");
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder, "bun"), new byte[1000]);
        await File.WriteAllBytesAsync(Path.Combine(folder, "install.json"), new byte[234]);
        _bun.InstallAt("1.4.2", Path.Combine(folder, "bun"));
        var runtime = NewRuntime();

        var view = await runtime.GetViewAsync(Alice, CancellationToken.None);

        view.Bun.ShouldBe(new ModsRuntimeBun(Path.Combine(folder, "bun"), "~/.weave/runtimes/bun/1.4.2/bun".Replace('/', Path.DirectorySeparatorChar), "installed", "1.4.2", true, null));
        view.InstalledSize.ShouldBe(1234);
        view.Release.Version.ShouldBe("1.4.2");
        view.Release.OldestSafe.ShouldBe("1.4.0");
        view.Release.Size.ShouldBe(36_646_949);
        view.Release.HasBuild.ShouldBeTrue();
        view.Release.Source.ShouldBe("github.com/oven-sh/bun");
        view.Release.InstallFolder.ShouldBe(Path.Combine("~", ".weave", "runtimes", "bun", "1.4.2"));
        view.ConfiguredPath.ShouldBeNull();
        view.ConfiguredInConfig.ShouldBeFalse();
        view.Update.ShouldBeNull();
        view.Job.ShouldBeNull();
    }

    [Fact]
    public async Task The_view_with_nothing_installed_has_no_bun_and_no_size()
    {
        var view = await NewRuntime().GetViewAsync(Alice, CancellationToken.None);

        view.Bun.ShouldBeNull();
        view.InstalledSize.ShouldBe(0);
        view.Update.ShouldBeNull();
    }

    [Fact]
    public async Task The_view_knows_a_release_with_no_build_for_this_computer()
    {
        _releases.Current = new BunRelease("1.4.2", []);

        var view = await NewRuntime().GetViewAsync(Alice, CancellationToken.None);

        view.Release.HasBuild.ShouldBeFalse();
        view.Release.Size.ShouldBeNull();
    }

    [Fact]
    public async Task The_view_offers_an_update_and_calls_it_a_security_one_when_the_bun_is_unsafe()
    {
        _releases.Current = Release("1.4.3", oldestSafe: "1.4.2");
        _bun.Install("1.4.1");
        var runtime = NewRuntime();

        var view = await runtime.GetViewAsync(Alice, CancellationToken.None);

        view.Update.ShouldBe(new ModsRuntimeUpdate("1.4.3", Security: true));
        view.Bun!.Safe.ShouldBeFalse();
        view.Bun.Message.ShouldNotBeNull();
    }

    [Fact]
    public async Task The_view_shows_a_safe_older_bun_as_a_plain_update()
    {
        _releases.Current = Release("1.4.3");
        _bun.Install("1.4.2");

        var view = await NewRuntime().GetViewAsync(Alice, CancellationToken.None);

        view.Update.ShouldBe(new ModsRuntimeUpdate("1.4.3", Security: false));
    }

    [Fact]
    public async Task The_view_shows_the_users_own_bun_and_a_path_that_cannot_run_with_why()
    {
        _setting.Seed(Alice, "/opt/tools/bun/bin/bun");
        _bun.UserBun[Alice] = new BunLocation("/opt/tools/bun/bin/bun", BunSources.Configured, "1.4.5");
        var runtime = NewRuntime();

        var working = await runtime.GetViewAsync(Alice, CancellationToken.None);
        working.ConfiguredPath.ShouldBe("/opt/tools/bun/bin/bun");
        working.Bun!.Source.ShouldBe("configured");
        working.ConfiguredError.ShouldBeNull();

        _bun.UserBun.Remove(Alice);
        _bun.CandidateMessage = "There's no file at /opt/tools/bun/bin/bun.";
        var broken = await runtime.GetViewAsync(Alice, CancellationToken.None);
        broken.Bun.ShouldBeNull();
        broken.ConfiguredError.ShouldBe("There's no file at /opt/tools/bun/bin/bun.");
        (await runtime.GetViewAsync("bob", CancellationToken.None)).ConfiguredPath.ShouldBeNull();
    }

    [Fact]
    public async Task The_view_says_when_configuration_sets_the_path_and_why_it_cannot_run()
    {
        _setting.FromConfiguration = "/etc/fleet/bun";
        _bun.EnsureError = "Fleet:Harness:BunPath is /etc/fleet/bun, which doesn't exist.";

        var view = await NewRuntime().GetViewAsync(Alice, CancellationToken.None);

        view.ConfiguredInConfig.ShouldBeTrue();
        view.ConfiguredPath.ShouldBe("/etc/fleet/bun");
        view.ConfiguredError.ShouldBe("Fleet:Harness:BunPath is /etc/fleet/bun, which doesn't exist.");
    }

    [Fact]
    public async Task The_view_names_a_mirror_as_the_source()
    {
        _options.Harness.BunDownloadBase = "https://mirror.example:8443/bun-files";

        var view = await NewRuntime().GetViewAsync(Alice, CancellationToken.None);

        view.Release.Source.ShouldBe("mirror.example:8443/bun-files");
    }

    [Theory]
    [InlineData("/home/u/.weave/x", "~/.weave/x")]
    [InlineData("/home/u", "~")]
    [InlineData("/home/user2/x", "/home/user2/x")]
    [InlineData("/opt/bun", "/opt/bun")]
    [InlineData("/home/u\\sub", "~\\sub")]
    public void Shortens_the_home_folder_to_a_tilde_whichever_separator_follows(string path, string shown)
    {
        var runtime = new ModsRuntime(
            _bun, _releases, _setting, _preferences, _safeMode, _events,
            _preferences.Scopes(), _preferences, _options, _clock, NullLogger<ModsRuntime>.Instance)
        {
            Home = "/home/u",
        };

        runtime.Display(path).ShouldBe(shown);
    }

    // -- helpers ---------------------------------------------------------------------------------------------------

    private static BunInstallJob Job(string phase, long received, string? message = null) =>
        new(phase, "1.4.2", message ?? "Installing the mod runtime…", received, 36_646_949);

    /// <summary>Plays the installer: scripted reports, an optional pause, and a configurable failure.</summary>
    private sealed class FakeBunRuntime : IBunRuntime
    {
        private readonly List<BunLocation> _installed = [];
        private HoldGate? _gate;

        public BunInstallJob? Job { get; private set; }
        public List<string> Ensured { get; } = [];
        public Action<Action<BunInstallJob>>? Script { get; set; }
        public (string Reason, string Message)? Fail { get; set; }
        public Dictionary<string, BunLocation> UserBun { get; } = [];
        public string? CandidateMessage { get; set; }
        public string? EnsureError { get; set; }

        public void Install(string version) => InstallAt(version, $"/bun/{version}/bun");

        public void InstallAt(string version, string path) => _installed.Add(new BunLocation(path, BunSources.Installed, version));

        public HoldGate Hold(Action<Action<BunInstallJob>>? script = null, bool waitForCancel = false) =>
            _gate = new HoldGate(script, waitForCancel);

        public Task<BunLocation?> FindAsync(BunRelease release, CancellationToken ct)
        {
            var wanted = _installed.FirstOrDefault(b => b.Version == release.Version);
            return Task.FromResult(wanted ?? _installed.OrderByDescending(b => BunVersion.Parse(b.Version)).FirstOrDefault());
        }

        public async Task<BunLocation?> FindForUserAsync(BunRelease release, string userId, CancellationToken ct)
            => UserBun.TryGetValue(userId, out var own) ? own : await FindAsync(release, ct);

        public async Task<Result<BunLocation>> EnsureAsync(BunRelease release, IProgress<BunInstallJob>? progress, CancellationToken ct)
        {
            if (EnsureError is not null)
                return new FleetError("Mods.Runtime", EnsureError);

            lock (Ensured)
                Ensured.Add(release.Version);

            void Report(BunInstallJob job)
            {
                Job = job;
                progress?.Report(job);
            }

            var gate = _gate;
            if (gate is not null)
            {
                gate.Reporter = Report;
                gate.Script?.Invoke(Report);
                gate.MarkStarted();
                if (gate.WaitForCancel)
                {
                    try
                    {
                        await Task.Delay(Timeout.Infinite, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        Report(new BunInstallJob(BunInstallPhases.Failed, release.Version, "The install was cancelled.", 0, null, BunInstallFailures.Cancelled));
                        throw;
                    }
                }
                else
                {
                    await gate.Released.WaitAsync(ct);
                }
            }

            if (Fail is { } failure)
            {
                Report(new BunInstallJob(BunInstallPhases.Failed, release.Version, failure.Message, 0, null, failure.Reason));
                return new FleetError("Mods.Runtime", failure.Message);
            }

            if (gate?.Script is not null)
            {
                // The gate's script played the reports.
            }
            else if (Script is not null)
            {
                Script(Report);
            }
            else
            {
                Report(Job_(BunInstallPhases.Downloading));
                Report(Job_(BunInstallPhases.Succeeded));
            }

            var location = new BunLocation($"/bun/{release.Version}/bun", BunSources.Installed, release.Version);
            _installed.Add(location);
            return location;

            BunInstallJob Job_(string phase) => new(phase, release.Version, phase == BunInstallPhases.Succeeded ? $"Installed Bun {release.Version}." : "Installing the mod runtime…", 0, null);
        }

        public Task<IReadOnlyList<BunCandidate>> FindOnMachineAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<BunCandidate>>([]);

        public Task<BunCandidate> CheckAsync(string path, CancellationToken ct)
            => Task.FromResult(new BunCandidate(path, path, null, BunCandidateStatuses.NotWorking, CandidateMessage));

        public IReadOnlyList<BunLocation> Installed() => [.. _installed.OrderByDescending(b => BunVersion.Parse(b.Version))];

        public Task<IReadOnlyList<string>> PruneAsync(IReadOnlyCollection<string> inUse, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);

        public BunSafety SafetyOf(BunLocation location, BunRelease release)
        {
            var version = BunVersion.Parse(location.Version);
            var safe = !version.IsOlderThan(BunVersion.Parse(release.OldestSafe));
            var update = location.Source == BunSources.Installed && version.IsOlderThan(BunVersion.Parse(release.Version));
            return new BunSafety(safe, update, safe ? null : $"Fleet's Bun {location.Version} needs a security fix.");
        }
    }

    private sealed class HoldGate(Action<Action<BunInstallJob>>? script, bool waitForCancel)
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Action<Action<BunInstallJob>>? Script { get; } = script;
        public bool WaitForCancel { get; } = waitForCancel;
        public Action<BunInstallJob>? Reporter { get; set; }
        public Task Started => _started.Task;
        public Task Released => _released.Task;
        public void MarkStarted() => _started.TrySetResult();
        public void Release() => _released.TrySetResult();
        public void Report(BunInstallJob job) => Reporter!(job);
    }

    private sealed class FakeBunReleases(BunRelease current) : IBunReleases
    {
        public BunRelease Current { get; set; } = current;
        public int Refreshes { get; private set; }
        public Action? OnRefresh { get; set; }
        public Task? RefreshTask { get; set; }

        public event EventHandler<BunReleaseChangedEventArgs>? Changed;

        public void RaiseChanged(BunRelease previous) => Changed?.Invoke(this, new BunReleaseChangedEventArgs(previous, Current));

        public Task RefreshAsync(CancellationToken ct)
        {
            Refreshes++;
            OnRefresh?.Invoke();
            return RefreshTask ?? Task.CompletedTask;
        }
    }

    /// <summary>Preferences of many users, read across all of them, written one at a time, and the scope that picks the user.</summary>
    private sealed class FakePreferences : IModsPreferenceReader, IBackgroundUserScope
    {
        private static readonly AsyncLocal<string?> Current = new();
        private readonly Dictionary<(string User, string Key), string> _values = [];

        public void Seed(string user, string key, string value) => _values[(user, key)] = value;

        public string? Get(string user, string key) => _values.GetValueOrDefault((user, key));

        public Task<IReadOnlyList<ModsUserPreference>> ListAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ModsUserPreference>>([.. _values.Select(v => new ModsUserPreference(v.Key.User, v.Key.Key, v.Value))]);

        public IDisposable Begin(string userId)
        {
            Current.Value = userId;
            return new Restore();
        }

        public IServiceScopeFactory Scopes() => TestServiceScopeFactory.Create(services =>
            services.AddScoped<IUserPreferenceRepository>(_ => new UserRepository(this, Current.Value!)));

        private sealed class Restore : IDisposable
        {
            public void Dispose() => Current.Value = null;
        }

        private sealed class UserRepository(FakePreferences store, string user) : IUserPreferenceRepository
        {
            public Task<string?> GetAsync(string key) => Task.FromResult(store.Get(user, key));

            public Task<IReadOnlyDictionary<string, string>> GetAllAsync()
                => Task.FromResult<IReadOnlyDictionary<string, string>>(store._values.Where(v => v.Key.User == user).ToDictionary(v => v.Key.Key, v => v.Value));

            public Task SetAsync(string key, string value)
            {
                store._values[(user, key)] = value;
                return Task.CompletedTask;
            }
        }
    }
}

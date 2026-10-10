using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using WeaveFleet.Application.Mods;
using WeaveFleet.Application.Mods.Host;

namespace WeaveFleet.Application.Tests.Mods.Host;

public sealed class ModHostSupervisorTests
{
    private const string User = "test-user";

    private readonly FakeFactory _factory = new();
    private readonly FakeGate _gate = new();
    private readonly FakeBun _bun = new();
    private readonly FakeFiles _files = new();
    private readonly InMemoryModVersionStore _store = new();
    private readonly FakeTimeProvider _time = new();
    private readonly ModHostSupervisor _supervisor;

    public ModHostSupervisorTests()
        => _supervisor = new ModHostSupervisor(User, new ModHostDependencies(new ModHostOptions(), _factory, _gate, _bun, _files, _store, _time, NullLogger.Instance));

    private static ModOff UserOff => new(ModOffBy.User, DateTimeOffset.UnixEpoch);

    private void Keep(ModOff? off = null)
        => _store.SeedHistory(User, new ModHistory("test-chips", 1, off, [new ModVersion(1, DateTimeOffset.UnixEpoch, "0.1.0", "abc", null, null, null, null)]));

    private Task EnsureAsync() => _supervisor.EnsureAsync(CancellationToken.None).Within();

    /// <summary>Completes when the host next reports <paramref name="state"/> (or already does).</summary>
    private Task<ModHostStatus> WaitFor(string state)
    {
        var seen = new TaskCompletionSource<ModHostStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        _supervisor.Changed += s =>
        {
            if (s.State == state)
                seen.TrySetResult(s);
        };
        if (_supervisor.GetStatus().State == state)
            seen.TrySetResult(_supervisor.GetStatus());
        return seen.Task.Within();
    }

    private async Task<FakeConnection> StartedAsync()
    {
        Keep();
        await EnsureAsync();
        return _factory.Connections[0];
    }

    [Fact]
    public async Task Never_starts_when_the_switch_is_off()
    {
        Keep();
        _gate.On = false;
        await EnsureAsync();
        _factory.Launches.ShouldBeEmpty();
        _supervisor.GetStatus().ShouldBe(ModHostStatus.Stopped with { Reason = "Mods are off." });
    }

    [Fact]
    public async Task Never_starts_in_safe_mode()
    {
        Keep();
        _gate.Safe = true;
        await EnsureAsync();
        _factory.Launches.ShouldBeEmpty();
        _supervisor.GetStatus().Reason.ShouldBe("Started without mods.");
    }

    [Fact]
    public async Task Never_starts_for_nothing_kept_or_drafted_or_only_mods_that_are_off()
    {
        await EnsureAsync();
        _supervisor.GetStatus().Reason.ShouldBe("No mod is kept or drafted.");

        Keep(UserOff);
        _store.SeedDraft(User, "ses_test1", "demo-mod", off: UserOff);
        _store.SeedDraft(User, "ses_test2", "other-mod", withManifest: false);
        await EnsureAsync();
        _factory.Launches.ShouldBeEmpty();
        _supervisor.GetStatus().State.ShouldBe(ModHostStates.Stopped);
    }

    [Fact]
    public async Task Starts_with_a_kept_mod()
    {
        var connection = await StartedAsync();
        var userKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(User)))[..16].ToLowerInvariant();
        _factory.Launches.ShouldBe([new ModHostLaunch("/bun/bin/bun", "/app/mods-host/host.js", _store.HostFolder(User), "9.9.9", userKey)]);
        var status = _supervisor.GetStatus();
        status.State.ShouldBe(ModHostStates.Running);
        status.ProcessId.ShouldBe(connection.ProcessId);
        status.HostVersion.ShouldBe("host-1");
        status.BunPath.ShouldBe("/bun/bin/bun");

        await EnsureAsync();
        _factory.Launches.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Starts_with_a_draft()
    {
        _store.SeedDraft(User, "ses_test1", "demo-mod");
        await EnsureAsync();
        _factory.Launches.Count.ShouldBe(1);
        _supervisor.GetStatus().State.ShouldBe(ModHostStates.Running);
    }

    [Fact]
    public async Task Stops_when_the_last_mod_goes_the_switch_turns_off_or_safe_mode_is_set()
    {
        var connection = await StartedAsync();
        Keep(UserOff);
        await EnsureAsync();
        connection.Shutdowns.ShouldBe([TimeSpan.FromSeconds(2)]);
        connection.Disposals.ShouldBe(1);
        _supervisor.GetStatus().Reason.ShouldBe("No mod is kept or drafted.");

        Keep();
        await EnsureAsync();
        _gate.On = false;
        await EnsureAsync();
        _factory.Connections[1].Shutdowns.Count.ShouldBe(1);
        _supervisor.GetStatus().Reason.ShouldBe("Mods are off.");

        _gate.On = true;
        await EnsureAsync();
        _gate.Safe = true;
        await EnsureAsync();
        _factory.Connections[2].Shutdowns.Count.ShouldBe(1);
        _supervisor.GetStatus().Reason.ShouldBe("Started without mods.");
    }

    [Fact]
    public async Task Is_not_ready_without_Bun_without_the_script_and_when_the_factory_refuses()
    {
        Keep();
        _bun.Location = null;
        await EnsureAsync();
        _supervisor.GetStatus().ShouldBe(new ModHostStatus(ModHostStates.NotReady, "The mod runtime (Bun) isn't installed yet.", null, null, null, 0));

        _bun.Location = new("/bun/bin/bun", "test", "1.3.0");
        _files.HostScript = null;
        await EnsureAsync();
        _supervisor.GetStatus().Reason.ShouldBe("Fleet can't find the mod host (mods-host/host.js).");

        _files.HostScript = "/app/mods-host/host.js";
        _factory.Fail = new ModHostNotReadyException("The mod host speaks another protocol.");
        await EnsureAsync();
        _supervisor.GetStatus().State.ShouldBe(ModHostStates.NotReady);
        _supervisor.GetStatus().Reason.ShouldBe("The mod host speaks another protocol.");
        _factory.Launches.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_crash_restarts_after_a_growing_wait_that_stops_at_30_seconds()
    {
        var connection = await StartedAsync();
        foreach (var (wait, n) in new[] { 0.5, 1, 2, 4, 8, 16, 30, 30 }.Select((w, i) => (w, i + 1)))
        {
            connection.Crash();
            var restarting = await WaitFor(ModHostStates.Restarting);
            restarting.Restarts.ShouldBe(n);
            _time.Advance(TimeSpan.FromSeconds(wait) - TimeSpan.FromMilliseconds(1));
            _factory.Launches.Count.ShouldBe(n);
            var next = _factory.NextStart();
            _time.Advance(TimeSpan.FromMilliseconds(1));
            connection = await next.Within();
            await WaitFor(ModHostStates.Running);
        }
        _factory.Connections[0].Disposals.ShouldBe(1);
    }

    [Fact]
    public async Task The_wait_starts_again_after_the_host_was_up_for_a_minute()
    {
        var connection = await StartedAsync();
        connection.Crash();
        await WaitFor(ModHostStates.Restarting);
        var next = _factory.NextStart();
        _time.Advance(TimeSpan.FromSeconds(0.5));
        connection = await next.Within();
        await WaitFor(ModHostStates.Running);

        _time.Advance(TimeSpan.FromSeconds(60));
        connection.Crash();
        await WaitFor(ModHostStates.Restarting);
        next = _factory.NextStart();
        _time.Advance(TimeSpan.FromSeconds(0.5));
        (await next.Within()).ShouldNotBeNull();
    }

    [Fact]
    public async Task Mods_going_away_while_waiting_cancels_the_restart()
    {
        var connection = await StartedAsync();
        connection.Crash();
        await WaitFor(ModHostStates.Restarting);
        Keep(UserOff);
        await EnsureAsync();
        _time.Advance(TimeSpan.FromMinutes(1));
        _factory.Launches.Count.ShouldBe(1);
        _supervisor.GetStatus().State.ShouldBe(ModHostStates.Stopped);
    }

    [Fact]
    public async Task A_request_that_times_out_kills_the_host_once_and_it_restarts()
    {
        var connection = await StartedAsync();
        connection.KillEndsProcess = false;
        connection.OnRequest = () => Task.FromException<JsonElement>(new TimeoutException());

        for (var i = 0; i < 2; i++)
        {
            var failure = await Should.ThrowAsync<ModHostNotReadyException>(_supervisor.RequestAsync("ping", default).Within());
            failure.Message.ShouldBe("The mod host didn't answer within 15 s; Fleet is restarting it.");
        }
        connection.Kills.ShouldBe(1);

        connection.Crash(137);
        await WaitFor(ModHostStates.Restarting);
        _supervisor.GetStatus().Restarts.ShouldBe(1);
        var next = _factory.NextStart();
        _time.Advance(TimeSpan.FromSeconds(0.5));
        (await next.Within()).ShouldNotBeSameAs(connection);
    }

    [Fact]
    public async Task A_timeout_that_ends_the_process_restarts_it_without_help()
    {
        var connection = await StartedAsync();
        connection.OnRequest = () => Task.FromException<JsonElement>(new TimeoutException());
        await Should.ThrowAsync<ModHostNotReadyException>(_supervisor.RequestAsync("ping", default).Within());
        await WaitFor(ModHostStates.Restarting);
        var next = _factory.NextStart();
        _time.Advance(TimeSpan.FromSeconds(0.5));
        await next.Within();
        connection.Kills.ShouldBe(1);
    }

    [Fact]
    public async Task A_request_answers_with_the_hosts_result_and_one_while_stopped_is_not_ready()
    {
        await Should.ThrowAsync<ModHostNotReadyException>(_supervisor.RequestAsync("ping", default).Within());

        var connection = await StartedAsync();
        connection.OnRequest = () => Task.FromResult(JsonSerializer.SerializeToElement(42));
        (await _supervisor.RequestAsync("ping", default).Within()).GetInt32().ShouldBe(42);

        Keep(UserOff);
        await EnsureAsync();
        var failure = await Should.ThrowAsync<ModHostNotReadyException>(_supervisor.RequestAsync("ping", default).Within());
        failure.Message.ShouldBe("No mod is kept or drafted.");
    }

    [Fact]
    public async Task One_ensure_runs_at_a_time()
    {
        Keep();
        _factory.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = _supervisor.EnsureAsync(CancellationToken.None);
        var second = _supervisor.EnsureAsync(CancellationToken.None);
        await WaitFor(ModHostStates.Starting);
        _factory.Hold.SetResult();
        await Task.WhenAll(first, second).Within();
        _factory.Launches.Count.ShouldBe(1);
        _supervisor.GetStatus().State.ShouldBe(ModHostStates.Running);
    }

    [Fact]
    public async Task The_hosts_own_calls_are_answered_as_unknown_for_now()
    {
        var calls = (IModHostCalls)_supervisor;
        var failure = await Should.ThrowAsync<ModHostRpcException>(calls.HandleRequestAsync("ui.set", default, CancellationToken.None).Within());
        failure.Code.ShouldBe(ModHostErrorCodes.MethodNotFound);
        Should.NotThrow(() => calls.HandleNotification("log", default));
    }

    [Fact]
    public async Task Shutdown_stops_the_host_and_nothing_restarts_it_afterwards()
    {
        var connection = await StartedAsync();
        await _supervisor.ShutdownAsync().Within();
        connection.Shutdowns.Count.ShouldBe(1);
        await EnsureAsync();
        _time.Advance(TimeSpan.FromMinutes(1));
        _factory.Launches.Count.ShouldBe(1);
        _supervisor.GetStatus().State.ShouldBe(ModHostStates.Stopped);
    }
}

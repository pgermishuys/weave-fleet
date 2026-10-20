using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Updates;
using WeaveFleet.Infrastructure.Updates;

namespace WeaveFleet.Infrastructure.Tests.Updates;

/// <summary>A Fleet restarts itself into its update only when its rule says so, nothing has worked for a minute, and the update is new and hasn't failed.</summary>
public sealed class SelfUpdateServiceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("fleet-self-update-").FullName;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
    private readonly List<int> _exits = [];
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly UpdateRuleStore _rules;
    private string? _staged = "99.0.0";
    private int _working;
    private bool _canRestart = true;

    public SelfUpdateServiceTests()
    {
        File.WriteAllText(Path.Combine(_root, "VERSION"), "0.59.0");
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _rules = new UpdateRuleStore(Path.Combine(_root, "fleet.update.json"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private SelfUpdateService Service()
    {
        var restart = new UpdateRestartService(
            new FleetOptions(), new UpdateFiles(_root), null!, new UpdateRestartGate(), null!, new StubLifetime(), _time,
            NullLogger<UpdateRestartService>.Instance, code => { _exits.Add(code); _exited.TrySetResult(); },
            name => _canRestart && name == UpdateRestartService.LauncherVariable ? "85" : null,
            () => _staged, () => Task.FromResult(_working), rules: _rules);
        return new SelfUpdateService(_rules, restart, new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), _time, NullLogger<SelfUpdateService>.Instance, () => Task.FromResult(_working));
    }

    [Theory]
    [InlineData("idle", 61, 12, true)]
    [InlineData("idle", 59, 12, false)]
    [InlineData("ask", 600, 3, false)]
    [InlineData(null, 600, 3, false)]
    [InlineData("overnight", 600, 12, false)]
    [InlineData("overnight", 600, 1, false)]
    [InlineData("overnight", 600, 2, true)]
    [InlineData("overnight", 600, 4, true)]
    [InlineData("overnight", 600, 5, false)]
    [InlineData("overnight", 30, 3, false)]
    public void The_rule_the_quiet_time_and_the_clock_decide(string? rule, int quietSeconds, int hour, bool expected)
        => SelfUpdateService.MayRestart(rule, TimeSpan.FromSeconds(quietSeconds), TimeSpan.FromHours(hour)).ShouldBe(expected);

    [Fact]
    public async Task It_waits_for_a_working_session_then_a_quiet_minute_before_restarting_and_records_why()
    {
        _rules.SetRule("idle");
        var service = Service();

        _working = 1;
        (await service.TickAsync()).ShouldBeFalse();
        _time.Advance(TimeSpan.FromSeconds(90));
        (await service.TickAsync()).ShouldBeFalse("a session is still working");

        _working = 0;
        _time.Advance(TimeSpan.FromSeconds(30));
        (await service.TickAsync()).ShouldBeFalse("it has been quiet for 30 seconds only");
        _time.Advance(TimeSpan.FromSeconds(31));
        (await service.TickAsync()).ShouldBeTrue();
        // The exit waits half a second for the reply to leave; advance until the delay has been set.
        while (!_exited.Task.IsCompleted)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Yield();
        }

        _exits.ShouldBe([UpdateRestartService.ExitCode]);

        _rules.Last.ShouldBeNull("not an update until the new version is up");
        _rules.CompleteUpdate("99.0.0");
        _rules.Last.ShouldNotBeNull().Trigger.ShouldBe("idle");
        _rules.Last.To.ShouldBe("99.0.0");
    }

    [Fact]
    public async Task Nothing_newer_a_failed_version_or_no_way_back_up_means_no_restart()
    {
        _rules.SetRule("idle");
        var service = Service();
        _time.Advance(TimeSpan.FromMinutes(5));

        _staged = null;
        (await service.TickAsync()).ShouldBeFalse();
        _staged = "0.59.0";
        (await service.TickAsync()).ShouldBeFalse("not newer");

        _staged = "99.0.0";
        File.WriteAllText(Path.Combine(_root, "update-failed.json"), """{"from":"0.59.0","to":"99.0.0","reason":"exited","at":"2026-10-20T09:00:00Z"}""");
        (await service.TickAsync()).ShouldBeFalse("that version already failed here");
        File.Delete(Path.Combine(_root, "update-failed.json"));

        _canRestart = false;
        (await Service().TickAsync()).ShouldBeFalse();
        _exits.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_rule_and_the_last_update_survive_a_restart_and_a_version_that_did_not_come_back_is_forgotten()
    {
        _rules.SetRule("overnight");
        _rules.RecordRestart(new LastUpdate("0.59.0", "99.0.0", "2026-10-20T03:12:00Z", "overnight"));

        var reopened = new UpdateRuleStore(Path.Combine(_root, "fleet.update.json"));
        reopened.Rule.ShouldBe("overnight");
        reopened.CompleteUpdate("0.59.0");
        reopened.Last.ShouldBeNull("it came back on the old version");

        reopened.RecordRestart(new LastUpdate("0.59.0", "99.0.0", "2026-10-20T03:12:00Z", "overnight"));
        reopened.CompleteUpdate("99.0.0");
        new UpdateRuleStore(Path.Combine(_root, "fleet.update.json")).Last!.Trigger.ShouldBe("overnight");
    }

    private sealed class StubLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication()
        {
        }
    }
}

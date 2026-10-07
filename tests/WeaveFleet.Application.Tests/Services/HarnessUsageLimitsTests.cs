using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Application.Tests.Services;

/// <summary>
/// <see cref="HarnessUsageLimits"/> keeps the latest usage limits each user's harnesses report, per window, and tells
/// that user's clients when they change.
/// </summary>
public sealed class HarnessUsageLimitsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeEventBroadcaster _broadcaster = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly HarnessUsageLimits _limits;

    public HarnessUsageLimitsTests()
        => _limits = new HarnessUsageLimits(_broadcaster, _time, NullLogger<HarnessUsageLimits>.Instance);

    [Fact]
    public async Task A_report_is_kept_per_harness_and_sent_to_the_users_clients()
    {
        await _limits.ObserveAsync("alice", "claude-code", Report(Window(UsageLimitWindows.FiveHour, 0.82, Now.AddHours(2))));

        var usage = _limits.Get("alice").ShouldHaveSingleItem();
        usage.HarnessType.ShouldBe("claude-code");
        usage.Windows.ShouldHaveSingleItem().Utilization.ShouldBe(0.82);
        _limits.Get("bob").ShouldBeEmpty();

        var sent = _broadcaster.Broadcasts.ShouldHaveSingleItem();
        sent.Topic.ShouldBe("sessions");
        sent.Type.ShouldBe(HarnessUsageLimits.ChangedEvent);
        sent.UserId.ShouldBe("alice");
        sent.Payload.GetProperty("harnessType").GetString().ShouldBe("claude-code");
        var window = sent.Payload.GetProperty("windows")[0];
        window.GetProperty("window").GetString().ShouldBe(UsageLimitWindows.FiveHour);
        window.GetProperty("utilization").GetDouble().ShouldBe(0.82);
        window.GetProperty("status").GetString().ShouldBe(UsageLimitStatuses.Allowed);
        window.GetProperty("resetsAt").GetDateTimeOffset().ShouldBe(Now.AddHours(2));
    }

    [Fact]
    public async Task A_later_report_replaces_only_the_windows_it_names()
    {
        await _limits.ObserveAsync("alice", "claude-code", Report(
            Window(UsageLimitWindows.FiveHour, 0.5, Now.AddHours(2)),
            Window(UsageLimitWindows.SevenDay, 0.6, Now.AddDays(3))));
        await _limits.ObserveAsync("alice", "claude-code", Report(Window(UsageLimitWindows.FiveHour, 1, Now.AddHours(2), UsageLimitStatuses.Rejected)));

        var windows = _limits.Get("alice").ShouldHaveSingleItem().Windows.ToDictionary(w => w.Window);
        windows[UsageLimitWindows.FiveHour].Status.ShouldBe(UsageLimitStatuses.Rejected);
        windows[UsageLimitWindows.SevenDay].Utilization.ShouldBe(0.6);
        _broadcaster.Broadcasts.Last().Payload.GetProperty("windows").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task The_same_report_again_isnt_sent_again()
    {
        await _limits.ObserveAsync("alice", "claude-code", Report(Window(UsageLimitWindows.FiveHour, 0.5, Now.AddHours(2))));
        await _limits.ObserveAsync("alice", "claude-code", Report(Window(UsageLimitWindows.FiveHour, 0.5, Now.AddHours(2))));

        _broadcaster.Broadcasts.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_window_that_has_reset_is_left_out()
    {
        await _limits.ObserveAsync("alice", "claude-code", Report(
            Window(UsageLimitWindows.FiveHour, 1, Now.AddHours(1), UsageLimitStatuses.Rejected),
            Window(UsageLimitWindows.SevenDay, 0.6, Now.AddDays(3))));

        _time.Advance(TimeSpan.FromHours(2));

        _limits.Get("alice").ShouldHaveSingleItem().Windows.ShouldHaveSingleItem().Window.ShouldBe(UsageLimitWindows.SevenDay);
    }

    [Fact]
    public async Task Nothing_is_kept_without_a_user_or_windows()
    {
        await _limits.ObserveAsync(null, "claude-code", Report(Window(UsageLimitWindows.FiveHour, 0.5, Now.AddHours(2))));
        await _limits.ObserveAsync("alice", "claude-code", Report());

        _limits.Get("alice").ShouldBeEmpty();
        _broadcaster.Broadcasts.ShouldBeEmpty();
    }

    private static UsageLimitReport Report(params UsageLimitWindow[] windows) => new() { Windows = windows };

    private static UsageLimitWindow Window(string window, double utilization, DateTimeOffset resetsAt, string status = UsageLimitStatuses.Allowed)
        => new() { Window = window, Utilization = utilization, ResetsAt = resetsAt, Status = status };
}

using Microsoft.Extensions.Time.Testing;
using Shouldly;
using WeaveFleet.Application.Automations;
using WeaveFleet.Application.Memory.Dreaming;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Memory;

public sealed class NightlyDreamingTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 27, 8, 0, 0, TimeSpan.Zero));
    private readonly InMemoryAutomationRepository _automations = new();
    private readonly InMemoryUserPreferenceRepository _preferences = new();
    private readonly InMemoryDreamStore _dreams = new();
    private readonly NightlyDreaming _sut;

    public NightlyDreamingTests()
    {
        _sut = new NightlyDreaming(_automations, _preferences, _dreams, new TestUserContext("owner"), _time);
    }

    [Fact]
    public async Task Turning_it_on_creates_the_built_in_automation_at_three()
    {
        var result = await _sut.SetAsync(new NightlyDreamingRequest(true, null, "Europe/Amsterdam", null, null, null));

        result.IsSuccess.ShouldBeTrue();
        var automation = _automations.All.ShouldHaveSingleItem();
        (automation.Builtin, automation.TargetType, automation.TriggerType, automation.TriggerConfig).ShouldBe(("dreaming", "dreaming", "schedule", "0 3 * * *"));
        (automation.TimeZone, automation.HarnessType, automation.IsEnabled, automation.UserId).ShouldBe(("Europe/Amsterdam", "opencode2", true, "owner"));
        (result.Value.Enabled, result.Value.Time, result.Value.BudgetTokens, result.Value.AutomationId).ShouldBe((true, "03:00", 300_000, automation.Id));
        result.Value.NextRunAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_new_time_changes_the_cron_and_restarts_the_schedule_from_now()
    {
        await _sut.SetAsync(new NightlyDreamingRequest(true, null, "UTC", null, null, null));

        var result = await _sut.SetAsync(new NightlyDreamingRequest(null, "02:30", null, null, null, null));

        var automation = _automations.All.ShouldHaveSingleItem();
        automation.TriggerConfig.ShouldBe("30 2 * * *");
        automation.UpdatedAt.ShouldBe(_time.GetUtcNow().UtcDateTime.ToString("O"));
        result.Value.Time.ShouldBe("02:30");
    }

    [Fact]
    public async Task A_budget_is_kept_in_a_preference_and_read_back()
    {
        var result = await _sut.SetAsync(new NightlyDreamingRequest(null, null, null, null, null, 500_000));

        (await _preferences.GetAsync(DreamRules.BudgetPreferenceKey)).ShouldBe("500000");
        result.Value.BudgetTokens.ShouldBe(500_000);
        (await _sut.GetAsync()).BudgetTokens.ShouldBe(500_000);
        _automations.All.ShouldBeEmpty();
    }

    [Fact]
    public async Task Turning_it_off_deletes_the_automation()
    {
        await _sut.SetAsync(new NightlyDreamingRequest(true, null, null, null, null, null));

        var result = await _sut.SetAsync(new NightlyDreamingRequest(false, null, null, null, null, null));

        result.Value.Enabled.ShouldBeFalse();
        result.Value.AutomationId.ShouldBeNull();
        (await _automations.GetBuiltinAsync(BuiltinAutomations.Dreaming)).ShouldBeNull();
    }

    [Theory]
    [InlineData("3am", null, null)]
    [InlineData("25:00", null, null)]
    [InlineData(null, "Mars/Olympus", null)]
    [InlineData(null, null, 5_000)]
    [InlineData(null, null, 50_000_000)]
    public async Task A_bad_time_zone_or_budget_is_refused_and_nothing_is_changed(string? time, string? zone, int? budget)
    {
        var result = await _sut.SetAsync(new NightlyDreamingRequest(true, time, zone, null, null, budget));

        result.IsFailure.ShouldBeTrue();
        _automations.All.ShouldBeEmpty();
        (await _preferences.GetAsync(DreamRules.BudgetPreferenceKey)).ShouldBeNull();
    }

    [Fact]
    public async Task Recent_holds_only_finished_dreams_a_night_started_in_the_last_day()
    {
        Dream Made(string id, string? runId, string status, TimeSpan ago) => new()
        {
            Id = id, Repository = "/work/orchard", RunId = runId, Status = status, Started = _time.GetUtcNow() - ago,
        };
        await _dreams.SaveAsync("owner", Made("night", "run-1", "done", TimeSpan.FromHours(5)));
        await _dreams.SaveAsync("owner", Made("by-hand", null, "done", TimeSpan.FromHours(4)));
        await _dreams.SaveAsync("owner", Made("going", "run-1", "running", TimeSpan.FromHours(1)));
        await _dreams.SaveAsync("owner", Made("old", "run-0", "done", TimeSpan.FromHours(40)));

        var view = await _sut.GetAsync();

        view.Recent.Select(d => d.Id).ShouldBe(["night"]);
        view.Enabled.ShouldBeFalse();
    }
}

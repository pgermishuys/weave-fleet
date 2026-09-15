using System.Text.Json;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Application.Tests.Services;

public sealed class AutomationServiceTests
{
    private readonly FakeAutomationRepository _repository = new();
    private readonly AutomationService _sut;

    public AutomationServiceTests()
    {
        _sut = new AutomationService(_repository, new TestUserContext());
    }

    [Fact]
    public async Task Create_switches_the_automation_on()
    {
        var result = await CreateScheduleAsync("0 9 * * 1", timeZone: null);

        result.IsSuccess.ShouldBeTrue();
        result.Value.IsEnabled.ShouldBeTrue();
        (await _repository.GetByIdAsync(result.Value.Id))!.IsEnabled.ShouldBeTrue();
    }

    [Fact]
    public async Task Create_keeps_the_time_zone()
    {
        var result = await CreateScheduleAsync("0 9 * * 1", timeZone: " Africa/Johannesburg ");

        result.IsSuccess.ShouldBeTrue();
        result.Value.TimeZone.ShouldBe("Africa/Johannesburg");
    }

    [Fact]
    public async Task Create_without_a_time_zone_leaves_it_empty_for_utc()
    {
        var result = await CreateScheduleAsync("0 9 * * 1", timeZone: "  ");

        result.IsSuccess.ShouldBeTrue();
        result.Value.TimeZone.ShouldBeNull();
    }

    [Fact]
    public async Task Create_rejects_an_unknown_time_zone()
    {
        var result = await CreateScheduleAsync("0 9 * * 1", timeZone: "Mars/Olympus_Mons");

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldStartWith("Validation.");
        result.Error.Description.ShouldContain("Mars/Olympus_Mons");
    }

    [Fact]
    public async Task Create_rejects_an_invalid_cron_expression()
    {
        var result = await CreateScheduleAsync("every monday", timeZone: "Africa/Johannesburg");

        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldContain("Invalid cron expression");
    }

    [Fact]
    public async Task Update_changes_the_time_zone()
    {
        var created = await CreateScheduleAsync("0 9 * * 1", timeZone: null);

        var result = await _sut.UpdateAsync(
            created.Value.Id, "Weekly digest", "Summarise the open PRs", "schedule", "0 9 * * 1",
            1, 10, 30, timeZone: "Europe/London");

        result.IsSuccess.ShouldBeTrue();
        (await _repository.GetByIdAsync(created.Value.Id))!.TimeZone.ShouldBe("Europe/London");
    }

    [Fact]
    public async Task Update_rejects_an_unknown_time_zone_and_keeps_the_old_one()
    {
        var created = await CreateScheduleAsync("0 9 * * 1", timeZone: "Africa/Johannesburg");

        var result = await _sut.UpdateAsync(
            created.Value.Id, "Weekly digest", "Summarise the open PRs", "schedule", "0 9 * * 1",
            1, 10, 30, timeZone: "Nowhere/Special");

        result.IsFailure.ShouldBeTrue();
        (await _repository.GetByIdAsync(created.Value.Id))!.TimeZone.ShouldBe("Africa/Johannesburg");
    }

    [Fact]
    public async Task Create_keeps_a_one_off_time_and_where_runs_happen()
    {
        var result = await _sut.CreateAsync(
            "Release notes check", "Check the release notes", "once", "2099-09-21T09:00", 1, 10, 30,
            workspaceId: "/home/me/source/weave-fleet", timeZone: "Europe/London", isolation: "worktree", baseBranch: "origin/main");

        result.IsSuccess.ShouldBeTrue();
        var stored = (await _repository.GetByIdAsync(result.Value.Id))!;
        (stored.TriggerType, stored.TriggerConfig, stored.Isolation, stored.BaseBranch)
            .ShouldBe(("once", "2099-09-21T09:00", "worktree", "origin/main"));
    }

    [Fact]
    public async Task Create_refuses_a_one_off_time_that_has_passed()
    {
        var result = await _sut.CreateAsync("Release notes check", "Check the release notes", "once", "2020-01-06T09:00", 1, 10, 30);

        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldContain("already passed");
    }

    [Theory]
    [InlineData(null, "worktree", null, "needs a folder")]
    [InlineData("/home/me/source/weave-fleet", "existing", "origin/main", "only be chosen when each run gets a new worktree")]
    [InlineData("/home/me/source/weave-fleet", "clone", null, "not 'clone'")]
    [InlineData("/home/me/source/weave-fleet", "worktree", "not a branch..", "not a valid branch name")]
    public async Task Create_checks_where_runs_happen(string? folder, string isolation, string? baseBranch, string expected)
    {
        var result = await _sut.CreateAsync(
            "Weekly digest", "Summarise the open PRs", "schedule", "0 9 * * 1", 1, 10, 30,
            workspaceId: folder, isolation: isolation, baseBranch: baseBranch);

        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldContain(expected);
    }

    [Fact]
    public async Task Create_refuses_an_unknown_target()
    {
        var result = await _sut.CreateAsync(
            "Weekly digest", "Summarise the open PRs", "schedule", "0 9 * * 1", 1, 10, 30, targetType: "every_session");

        result.IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task A_one_off_whose_time_has_passed_cant_be_switched_back_on()
    {
        var created = await _sut.CreateAsync("Release notes check", "Check the release notes", "once", "2099-09-21T09:00", 1, 10, 30);
        await _sut.DisableAsync(created.Value.Id);
        var stored = (await _repository.GetByIdAsync(created.Value.Id))!;
        stored.TriggerConfig = "2020-01-06T09:00";

        var result = await _sut.EnableAsync(created.Value.Id);

        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldContain("Pick a new time");
    }

    [Fact]
    public async Task An_automation_that_is_off_can_keep_a_one_off_time_that_has_passed()
    {
        var created = await _sut.CreateAsync("Release notes check", "Check the release notes", "once", "2099-09-21T09:00", 1, 10, 30);
        await _sut.DisableAsync(created.Value.Id);

        var result = await _sut.UpdateAsync(
            created.Value.Id, "Release notes check (renamed)", "Check the release notes", "once", "2020-01-06T09:00", 1, 10, 30);

        result.IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData("Check \"flaky\" tests")]
    [InlineData("Back\\slash")]
    [InlineData("Line\nbreak")]
    public void Session_source_is_valid_json_whatever_the_name(string name)
    {
        var automation = new Automation { Id = "auto-1", Name = name };

        var source = AutomationExecutionService.BuildSessionSource(automation, eventType: null);

        source.Input.GetProperty("automationId").GetString().ShouldBe("auto-1");
        source.Input.GetProperty("automationName").GetString().ShouldBe(name);
        source.Input.GetProperty("trigger").GetString().ShouldBe("schedule");
        source.Input.ValueKind.ShouldBe(JsonValueKind.Object);
    }

    private Task<WeaveFleet.Domain.Common.Result<Automation>> CreateScheduleAsync(string cron, string? timeZone) =>
        _sut.CreateAsync("Weekly digest", "Summarise the open PRs", "schedule", cron, 1, 10, 30, timeZone: timeZone);
}

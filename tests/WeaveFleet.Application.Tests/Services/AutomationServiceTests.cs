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

using Shouldly;
using WeaveFleet.Application.Skills;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Services;

public sealed class BuiltInSkillServiceTests
{
    private readonly InMemoryUserPreferenceRepository _preferences = new();
    private readonly BuiltInSkillService _service;

    public BuiltInSkillServiceTests()
    {
        _service = new BuiltInSkillService(new Catalog(), _preferences);
    }

    [Fact]
    public async Task ListAsync_shows_every_skill_off_until_the_user_turns_it_on()
    {
        var skills = await _service.ListAsync();

        skills.ShouldBe([
            new BuiltInSkillView("fleet-code-review", "Reviews.", Enabled: false),
            new BuiltInSkillView("fleet-simplify", "Simplifies.", Enabled: false),
        ]);
    }

    [Fact]
    public async Task SetEnabledAsync_turns_one_skill_on_and_off_and_keeps_the_others()
    {
        _preferences.Seed(BuiltInSkillService.PreferenceKey, "fleet-simplify,retired-skill");

        var on = await _service.SetEnabledAsync("fleet-code-review", enabled: true);

        on.Value.ShouldBe(new BuiltInSkillView("fleet-code-review", "Reviews.", Enabled: true));
        (await _preferences.GetAsync(BuiltInSkillService.PreferenceKey)).ShouldBe("fleet-code-review,fleet-simplify,retired-skill");

        await _service.SetEnabledAsync("fleet-simplify", enabled: false);

        (await _preferences.GetAsync(BuiltInSkillService.PreferenceKey)).ShouldBe("fleet-code-review,retired-skill");
        (await _service.ListAsync()).Select(skill => skill.Enabled).ShouldBe([true, false]);
    }

    [Fact]
    public async Task SetEnabledAsync_refuses_a_skill_Fleet_doesnt_ship()
    {
        var result = await _service.SetEnabledAsync("code-review", enabled: true);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("BuiltInSkill.NotFound");
        (await _preferences.GetAsync(BuiltInSkillService.PreferenceKey)).ShouldBeNull();
    }

    private sealed class Catalog : IBuiltInSkillCatalog
    {
        public IReadOnlyList<BuiltInSkill> Skills { get; } =
        [
            new("fleet-code-review", "Reviews."),
            new("fleet-simplify", "Simplifies."),
        ];
    }
}

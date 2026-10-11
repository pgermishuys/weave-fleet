using Shouldly;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Mods;
using WeaveFleet.Application.Skills;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Services;

/// <summary><c>fleet-mod</c> has no switch of its own: it comes with Mods, so an agent never has the tools without the guide.</summary>
public sealed class BuiltInSkillModsTests
{
    private static readonly string[] Shipped = ["fleet-mod", "fleet-run"];

    private readonly InMemoryUserPreferenceRepository _preferences = new();

    private BuiltInSkillService ServiceWith(bool modsOn)
    {
        _preferences.Seed(ModsFeature.PreferenceKey, modsOn ? "true" : "false");
        var user = new TestUserContext("owner-1");
        var mods = new ModsFeature(new FleetOptions(), _preferences, new ModsSafeMode(), user);
        return new BuiltInSkillService(new Catalog(), _preferences, user: user, mods: mods);
    }

    [Fact]
    public async Task Session_skills_include_fleet_mod_while_Mods_is_on_even_when_not_chosen()
    {
        _preferences.Seed(BuiltInSkillService.PreferenceKey, "fleet-run");

        var skills = await BuiltInSkillService.GetSessionSkillsAsync(_preferences, null, "owner-1", Shipped, mods: true);

        skills.Select(skill => skill.Name).ShouldBe(["fleet-mod", "fleet-run"]);
    }

    [Fact]
    public async Task Session_skills_drop_fleet_mod_while_Mods_is_off_even_when_chosen()
    {
        _preferences.Seed(BuiltInSkillService.PreferenceKey, "fleet-mod,fleet-run");

        var skills = await BuiltInSkillService.GetSessionSkillsAsync(_preferences, null, "owner-1", Shipped, mods: false);

        skills.Select(skill => skill.Name).ShouldBe(["fleet-run"]);
    }

    [Fact]
    public async Task ListAsync_marks_fleet_mod_as_coming_with_Mods_and_leaves_the_rest()
    {
        (await ServiceWith(modsOn: true).ListAsync()).ShouldBe([
            new BuiltInSkillView("fleet-mod", "Mods.", Enabled: true, WithMods: true),
            new BuiltInSkillView("fleet-run", "Runs.", Enabled: false),
        ]);
        (await ServiceWith(modsOn: false).ListAsync()).ShouldBe([
            new BuiltInSkillView("fleet-mod", "Mods.", Enabled: false, WithMods: true),
            new BuiltInSkillView("fleet-run", "Runs.", Enabled: false),
        ]);
    }

    [Fact]
    public async Task SetEnabledAsync_refuses_fleet_mod_because_it_has_no_switch()
    {
        var result = await ServiceWith(modsOn: true).SetEnabledAsync("fleet-mod", enabled: false);

        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldContain("Mods");
        (await _preferences.GetAsync(BuiltInSkillService.PreferenceKey)).ShouldBeNull();
    }

    [Fact]
    public async Task ModsChangedAsync_tells_every_harness()
    {
        var registry = new FakeHarnessRegistry();
        var opencode2 = new FakeHarnessRuntime("opencode2");
        registry.Register(new FakeHarness("opencode2", "OpenCode 2"));
        registry.Register(opencode2);
        var service = new BuiltInSkillService(new Catalog(), _preferences, registry, new TestUserContext("owner-1"));

        await service.ModsChangedAsync();

        opencode2.BuiltInSkillChanges.ShouldBe(["owner-1"]);
    }

    private sealed class Catalog : IBuiltInSkillCatalog
    {
        public IReadOnlyList<BuiltInSkill> Skills { get; } = [new("fleet-mod", "Mods."), new("fleet-run", "Runs.")];
    }
}

using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Mods;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Mods;

public sealed class ModsFeatureTests
{
    private readonly InMemoryUserPreferenceRepository _preferences = new();
    private readonly ModsSafeMode _safeMode = new();

    private ModsFeature Feature(bool option, string userId = "test-user")
    {
        var options = new FleetOptions();
        options.Harness.Mods = option;
        return new ModsFeature(options, _preferences, _safeMode, new TestUserContext(userId));
    }

    [Fact]
    public async Task The_option_decides_until_the_user_chooses()
    {
        (await Feature(option: false).IsSwitchedOnAsync()).ShouldBeFalse();
        (await Feature(option: true).IsSwitchedOnAsync()).ShouldBeTrue();
    }

    [Fact]
    public async Task The_preference_wins_over_the_option_both_ways()
    {
        _preferences.Seed(ModsFeature.PreferenceKey, "true");
        (await Feature(option: false).IsSwitchedOnAsync()).ShouldBeTrue();

        _preferences.Seed(ModsFeature.PreferenceKey, "false");
        (await Feature(option: true).IsSwitchedOnAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task Safe_mode_stops_mods_running_but_not_the_switch()
    {
        var feature = Feature(option: true);
        (await feature.IsEnabledAsync()).ShouldBeTrue();

        _safeMode.Set("test-user", true);

        (await feature.IsSwitchedOnAsync()).ShouldBeTrue();
        (await feature.IsEnabledAsync()).ShouldBeFalse();

        _safeMode.Set("test-user", false);
        (await feature.IsEnabledAsync()).ShouldBeTrue();
    }

    [Fact]
    public async Task Nothing_is_enabled_while_the_switch_is_off_whatever_safe_mode_says()
    {
        (await Feature(option: false).IsEnabledAsync()).ShouldBeFalse();
    }

    [Fact]
    public void Safe_mode_starts_off()
    {
        new ModsSafeMode().IsOn("test-user").ShouldBeFalse();
    }

    [Fact]
    public async Task Safe_mode_is_per_user()
    {
        _safeMode.Set("alice", true);

        (await Feature(option: true, userId: "alice").IsEnabledAsync()).ShouldBeFalse();
        (await Feature(option: true, userId: "alice").IsSwitchedOnAsync()).ShouldBeTrue();
        (await Feature(option: true, userId: "bob").IsEnabledAsync()).ShouldBeTrue();
        _safeMode.IsOn("bob").ShouldBeFalse();
    }
}

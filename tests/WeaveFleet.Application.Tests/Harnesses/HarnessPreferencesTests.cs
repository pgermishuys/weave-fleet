using WeaveFleet.Application.Harnesses;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Application.Tests.Harnesses;

public sealed class HarnessPreferencesTests
{
    private static HarnessInfo Harness(string type, bool available = true) =>
        HarnessInfo.From(type, type, new HarnessCapabilities(),
            available ? HarnessAvailability.Ready("1.0.0", $"/home/you/bin/{type}") : HarnessAvailability.NotInstalled($"{type} isn't installed."));

    private static readonly Dictionary<string, string> NoPreferences = [];

    [Fact]
    public void the_users_pick_wins_even_when_it_isnt_ready()
    {
        var preferences = new Dictionary<string, string> { [HarnessPreferences.DefaultHarnessKey] = "pi" };

        HarnessPreferences.DefaultHarness(preferences, [Harness("opencode2"), Harness("pi", available: false)]).ShouldBe("pi");
    }

    [Fact]
    public void without_a_pick_it_is_the_first_ready_harness_in_fleets_order()
    {
        HarnessPreferences.DefaultHarness(NoPreferences, [Harness("opencode2"), Harness("opencode")]).ShouldBe("opencode2");
        HarnessPreferences.DefaultHarness(NoPreferences, [Harness("opencode2", available: false), Harness("opencode")]).ShouldBe("opencode");
    }

    [Fact]
    public void a_harness_the_user_turned_off_is_skipped()
    {
        var preferences = new Dictionary<string, string> { ["opencode2.enabled"] = "false" };

        HarnessPreferences.DefaultHarness(preferences, [Harness("opencode2"), Harness("claude-code")]).ShouldBe("claude-code");
    }

    [Fact]
    public void with_none_ready_or_none_known_it_is_opencode_2()
    {
        HarnessPreferences.DefaultHarness(NoPreferences, [Harness("opencode", available: false)]).ShouldBe("opencode2");
        HarnessPreferences.DefaultHarness(NoPreferences, harnesses: null).ShouldBe("opencode2");
        HarnessPreferences.DefaultHarness(
            new Dictionary<string, string> { [HarnessPreferences.DefaultHarnessKey] = " " }, harnesses: null).ShouldBe("opencode2");
    }
}

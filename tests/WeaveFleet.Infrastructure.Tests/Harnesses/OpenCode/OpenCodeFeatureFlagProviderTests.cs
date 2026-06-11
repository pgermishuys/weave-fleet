using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode;

public sealed class OpenCodeFeatureFlagProviderTests
{
    [Fact]
    public async Task pooled_opencode_harness_defaults_to_off()
    {
        var preferences = new InMemoryUserPreferenceRepository();
        var provider = CreateProvider(new FleetOptions(), preferences);

        var enabled = await provider.IsPooledOpenCodeHarnessEnabledAsync("user-1", CancellationToken.None);

        enabled.ShouldBeFalse();
    }

    [Fact]
    public async Task pooled_opencode_harness_can_be_enabled_from_user_settings()
    {
        var preferences = new InMemoryUserPreferenceRepository();
        await preferences.SetAsync(OpenCodeFeatureFlagProvider.PooledOpenCodeHarnessPreferenceKey, "true");
        var provider = CreateProvider(new FleetOptions(), preferences);

        var enabled = await provider.IsPooledOpenCodeHarnessEnabledAsync("user-1", CancellationToken.None);

        enabled.ShouldBeTrue();
    }

    [Fact]
    public async Task pooled_opencode_harness_user_settings_override_configuration()
    {
        var preferences = new InMemoryUserPreferenceRepository();
        await preferences.SetAsync(OpenCodeFeatureFlagProvider.PooledOpenCodeHarnessPreferenceKey, "false");
        var provider = CreateProvider(
            new FleetOptions { Harness = new HarnessOptions { PooledOpenCodeHarness = true } },
            preferences);

        var enabled = await provider.IsPooledOpenCodeHarnessEnabledAsync("user-1", CancellationToken.None);

        enabled.ShouldBeFalse();
    }

    [Fact]
    public async Task pooled_opencode_harness_uses_configuration_when_user_setting_is_absent()
    {
        var preferences = new InMemoryUserPreferenceRepository();
        var provider = CreateProvider(
            new FleetOptions { Harness = new HarnessOptions { PooledOpenCodeHarness = true } },
            preferences);

        var enabled = await provider.IsPooledOpenCodeHarnessEnabledAsync("user-1", CancellationToken.None);

        enabled.ShouldBeTrue();
    }

    private static OpenCodeFeatureFlagProvider CreateProvider(
        FleetOptions options,
        IUserPreferenceRepository preferences)
    {
        return new OpenCodeFeatureFlagProvider(
            options,
            TestServiceScopeFactory.Create(services => services.AddSingleton(preferences)));
    }
}

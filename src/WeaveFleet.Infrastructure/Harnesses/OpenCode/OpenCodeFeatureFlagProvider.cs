using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode;

internal sealed class OpenCodeFeatureFlagProvider(
    FleetOptions options,
    IServiceScopeFactory scopeFactory)
{
    internal const string PooledOpenCodeHarnessPreferenceKey = "PooledOpenCodeHarness";

    public async Task<bool> IsPooledOpenCodeHarnessEnabledAsync(string userId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using var backgroundScope = BackgroundUserContext.BeginScope(userId);
        using var serviceScope = scopeFactory.CreateScope();
        var preferences = serviceScope.ServiceProvider.GetRequiredService<IUserPreferenceRepository>();
        var value = await preferences.GetAsync(PooledOpenCodeHarnessPreferenceKey).ConfigureAwait(false);

        return string.IsNullOrWhiteSpace(value)
            ? options.Harness.PooledOpenCodeHarness
            : string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }
}

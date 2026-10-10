using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Runtimes;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Runtimes;

/// <summary>
/// <see cref="IBunPathSetting"/> over the configuration and each user's <see cref="BunPathPreference.Key"/> preference.
/// Reads and writes run as the user in a scope of their own, so this can be a singleton.
/// </summary>
internal sealed class BunPathSetting(FleetOptions options, IServiceScopeFactory scopes, IBackgroundUserScope users) : IBunPathSetting
{
    /// <inheritdoc />
    public string? FromConfiguration => string.IsNullOrWhiteSpace(options.Harness.BunPath) ? null : options.Harness.BunPath;

    /// <inheritdoc />
    public async Task<string?> GetAsync(string userId, CancellationToken ct)
    {
        if (FromConfiguration is { } configured)
            return configured;

        using var scope = scopes.CreateScope();
        using (users.Begin(userId))
        {
            var saved = await scope.ServiceProvider.GetRequiredService<IUserPreferenceRepository>()
                .GetAsync(BunPathPreference.Key).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(saved) ? null : saved;
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(string userId, string? path, CancellationToken ct)
    {
        if (FromConfiguration is not null)
            throw new InvalidOperationException("Fleet's configuration sets Fleet:Harness:BunPath, so a user's Bun can't be saved.");

        using var scope = scopes.CreateScope();
        using (users.Begin(userId))
        {
            await scope.ServiceProvider.GetRequiredService<IUserPreferenceRepository>()
                .SetAsync(BunPathPreference.Key, string.IsNullOrWhiteSpace(path) ? "" : path).ConfigureAwait(false);
        }
    }
}

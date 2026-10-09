using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Recaps;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Users;

namespace WeaveFleet.Infrastructure.Recaps;

/// <summary>
/// Reads the "Session recap" setting for a session's owner. Off unless they turned it on: each recap is
/// an extra request to the session's model.
/// </summary>
internal sealed class RecapPreference(IServiceScopeFactory scopeFactory) : IRecapPreference
{
    internal const string PreferenceKey = "SessionRecap";

    public async Task<bool> IsEnabledAsync(string userId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using var backgroundScope = BackgroundUserContext.BeginScope(userId);
        using var serviceScope = scopeFactory.CreateScope();
        var preferences = serviceScope.ServiceProvider.GetRequiredService<IUserPreferenceRepository>();
        var value = await preferences.GetAsync(PreferenceKey).ConfigureAwait(false);

        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }
}

using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Recaps;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Services;

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

/// <summary>
/// Reads the "Desktop notifications" setting for a session's owner. Off unless they turned it on: the
/// browser has to ask for permission before it can show one anyway.
/// </summary>
internal sealed class NotificationPreference(IServiceScopeFactory scopeFactory) : INotificationPreference
{
    internal const string PreferenceKey = "DesktopNotifications";

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

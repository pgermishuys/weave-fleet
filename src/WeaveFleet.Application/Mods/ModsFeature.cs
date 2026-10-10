using System.Collections.Concurrent;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Mods;

/// <summary>Whether Mods are on for the current user (Settings → Experimental), and whether any may run.</summary>
public sealed class ModsFeature(FleetOptions options, IUserPreferenceRepository preferences, ModsSafeMode safeMode, IUserContext user)
{
    public const string PreferenceKey = "Mods";

    public const string TurnedOffMessage = "Mods are turned off in Fleet's Settings.";

    /// <summary>The user's choice in Settings, or the option when they haven't made one. Settings and the API use this.</summary>
    public async Task<bool> IsSwitchedOnAsync()
    {
        var value = await preferences.GetAsync(PreferenceKey).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(value)
            ? options.Harness.Mods
            : string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Switched on and not in safe mode: what the mod host checks before it runs any mod.</summary>
    public async Task<bool> IsEnabledAsync() => !safeMode.IsOn(user.UserId) && await IsSwitchedOnAsync().ConfigureAwait(false);
}

/// <summary>
/// "Start without mods", per user: while it's set for a user, none of their mods run; other users' mods still do. Held in
/// memory, so it lasts until Fleet restarts or the user turns mods back on.
/// </summary>
public sealed class ModsSafeMode
{
    private readonly ConcurrentDictionary<string, bool> _users = new(StringComparer.Ordinal);

    public bool IsOn(string userId) => _users.ContainsKey(userId);

    public void Set(string userId, bool on)
    {
        if (on)
            _users[userId] = true;
        else
            _users.TryRemove(userId, out _);
    }
}

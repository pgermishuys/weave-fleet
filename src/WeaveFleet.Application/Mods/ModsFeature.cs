using WeaveFleet.Application.Configuration;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Mods;

/// <summary>Whether Mods are on for the current user (Settings → Experimental), and whether any may run.</summary>
public sealed class ModsFeature(FleetOptions options, IUserPreferenceRepository preferences, ModsSafeMode safeMode)
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
    public async Task<bool> IsEnabledAsync() => !safeMode.IsOn && await IsSwitchedOnAsync().ConfigureAwait(false);
}

/// <summary>
/// "Start without mods": while it's set, no mod runs anywhere. Held in memory, so it lasts until Fleet restarts or the
/// user turns mods back on.
/// </summary>
public sealed class ModsSafeMode
{
    private volatile bool _on;

    public bool IsOn => _on;

    public void Set(bool on) => _on = on;
}

using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Machines;

/// <summary>
/// "Your Fleet" (Settings → Labs): every machine keeps the list of the machines you've connected, and a screen
/// connected to any of them gets its own key for the others. Experimental, and off unless turned on; off, Fleet
/// behaves as it did before. See .weave/plans/machines-setup-journeys.md.
/// </summary>
public static class YourFleet
{
    /// <summary>The user preference that turns it on.</summary>
    public const string PreferenceKey = "YourFleet";

    /// <summary>The id a screen (a browser or the desktop app's window) gives itself, so its keys can be told apart and replaced.</summary>
    public const int MaxScreenIdLength = 64;

    /// <summary>The prefix of the grant row's device id for a screen, which isn't a paired device.</summary>
    public const string ScreenPrefix = "screen:";

    public static bool IsValidScreenId(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= MaxScreenIdLength && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}

/// <summary>Whether Your Fleet is on for the current user.</summary>
public sealed class YourFleetFeature(IUserPreferenceRepository preferences)
{
    public async Task<bool> IsEnabledAsync() =>
        string.Equals(await preferences.GetAsync(YourFleet.PreferenceKey).ConfigureAwait(false), "true", StringComparison.OrdinalIgnoreCase);
}

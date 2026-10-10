namespace WeaveFleet.Application.Mods;

/// <summary>A stored preference of one user.</summary>
public sealed record ModsUserPreference(string UserId, string Key, string Value);

/// <summary>
/// Reads the Mods switch and own-Bun preferences of every user at once, which Fleet's per-user repository can't:
/// whether anyone's mods need the runtime is a question about all of them.
/// </summary>
public interface IModsPreferenceReader
{
    /// <summary>Every user's stored <see cref="ModsFeature.PreferenceKey"/> and <c>ModsBunPath</c> preference.</summary>
    Task<IReadOnlyList<ModsUserPreference>> ListAsync(CancellationToken ct);
}

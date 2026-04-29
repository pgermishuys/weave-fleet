namespace WeaveFleet.Application.Configuration;

/// <summary>
/// Default filesystem paths used by Fleet when no explicit configuration is provided.
/// Anchors data under the platform-specific local app data directory so it doesn't follow
/// the process working directory.
/// </summary>
public static class FleetPaths
{
    /// <summary>
    /// Per-user local app data directory for Fleet:
    /// <c>%LOCALAPPDATA%\WeaveFleet</c> on Windows, <c>~/.local/share/WeaveFleet</c> on Linux,
    /// <c>~/Library/Application Support/WeaveFleet</c> on macOS.
    /// </summary>
    public static string DefaultAppDataDirectory { get; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WeaveFleet");
}

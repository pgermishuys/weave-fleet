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
    public static string DefaultAppDataDirectory { get; } = ResolveDefaultAppDataDirectory();

    // DoNotVerify: without it .NET returns "" when the folder doesn't exist yet (a new HOME has no ~/.local/share),
    // and Fleet then wrote to ./WeaveFleet under whatever folder it was started from.
    internal static string ResolveDefaultAppDataDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.DoNotVerify),
            "WeaveFleet");
}

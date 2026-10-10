using System.Runtime.InteropServices;

namespace WeaveFleet.Application.Runtimes;

/// <summary>One platform's Bun archive in a release.</summary>
/// <param name="Rid">The .NET runtime identifier it runs on, e.g. <c>linux-x64</c>.</param>
/// <param name="FileName">The release asset, e.g. <c>bun-linux-x64-baseline.zip</c>. The archive holds one folder
/// named after it (without <c>.zip</c>) with <c>bun</c> or <c>bun.exe</c> inside.</param>
/// <param name="Sha256">The asset's sha256 in lowercase hex, from the release's <c>SHASUMS256.txt</c>.</param>
public sealed record BunAsset(string Rid, string FileName, string Sha256)
{
    /// <summary>The folder inside the archive that holds the executable.</summary>
    public string Folder => Path.GetFileNameWithoutExtension(FileName);

    /// <summary>The executable's file name on this asset's platform.</summary>
    public string ExecutableName => Rid.StartsWith("win-", StringComparison.Ordinal) ? "bun.exe" : "bun";
}

/// <summary>
/// A Bun release Fleet can install, with the archive and sha256 for each platform it supports. Fleet's own manifest
/// says which release it wants; <see cref="Pinned"/> is the one built into Fleet, used when there's no manifest.
/// </summary>
/// <param name="Version">The release, e.g. <c>1.4.2</c>.</param>
/// <param name="Assets">The archive for each platform.</param>
public sealed record BunRelease(string Version, IReadOnlyList<BunAsset> Assets)
{
    /// <summary>The oldest Bun mods run on. A Bun older than this is never used, whatever its source.</summary>
    public const string MinimumVersion = "1.4.0";

    /// <summary>Where Bun's releases are downloaded from: <c>{base}/bun-v{version}/{asset}</c>.</summary>
    public static readonly Uri GitHubDownloads = new("https://github.com/oven-sh/bun/releases/download/");

    /// <summary>
    /// The oldest version that's safe to run. A Bun older than this still runs mods, but Fleet warns, and replaces it
    /// when it's Fleet's own. Fleet decides this when it bumps its manifest; Bun publishes no security advisories.
    /// </summary>
    public string OldestSafe { get; init; } = MinimumVersion;

    /// <summary>Why this release is recommended, in a sentence, e.g. what it fixes. Shown with security warnings.</summary>
    public string? Note { get; init; }

    /// <summary>
    /// The Bun built into Fleet, used until a manifest names another. The x64 builds are the <c>-baseline</c> ones,
    /// which run on CPUs without AVX2 (older machines, and x64 under Rosetta or some VMs). The sha256 values are from
    /// the release's <c>SHASUMS256.txt</c>, which <c>BunReleaseTests</c> checks them against.
    /// </summary>
    public static BunRelease Pinned { get; } = new("1.4.2",
    [
        new("linux-x64", "bun-linux-x64-baseline.zip", "c678040f14fe0440eb839d37cbd0ce4c051a32da72806ac97de6a6aab6bf728f"),
        new("linux-arm64", "bun-linux-aarch64.zip", "54328bbc2d9c8e0c9f892c544d66c57a83b84139e34909e5ee81758f1ac8fda7"),
        new("osx-x64", "bun-darwin-x64-baseline.zip", "bad5bbd6cf14d0980d115f5954c9ff904df619d5e994d2da1ffccd3f316300b0"),
        new("osx-arm64", "bun-darwin-aarch64.zip", "90987a3a16d7db556d886ac3d551e7b6d3edf0a1cf43acaed622e8676be1d12f"),
        new("win-x64", "bun-windows-x64-baseline.zip", "78c221c2376f79731ccf4e4af0b3bb46d81fefa3296c5abee09ad8a1b21e68c6"),
        new("win-arm64", "bun-windows-aarch64.zip", "a7a16b876a305fd1029c66dbd27007b4f6112ae896532f675878731a21e50cfd"),
    ]);

    /// <summary>The asset for <paramref name="rid"/>, or <see langword="null"/> when this release has none.</summary>
    public BunAsset? AssetFor(string rid) => Assets.FirstOrDefault(asset => asset.Rid == rid);

    /// <summary>Where to download <paramref name="asset"/> from.</summary>
    public Uri DownloadUrl(Uri downloadBase, BunAsset asset) => new(downloadBase, $"bun-v{Version}/{asset.FileName}");

    /// <summary>The runtime identifier of the machine Fleet runs on, e.g. <c>linux-x64</c> or <c>osx-arm64</c>.</summary>
    public static string CurrentRid()
    {
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            var other => other.ToString().ToLowerInvariant(),
        };
        return $"{os}-{arch}";
    }
}

/// <summary>The Bun release Fleet wants now: its manifest's, or <see cref="BunRelease.Pinned"/> without one.</summary>
public interface IBunReleases
{
    /// <summary>The release to install and to judge installed and configured Buns against.</summary>
    BunRelease Current { get; }

    /// <summary>
    /// Raised after <see cref="Current"/> changes: a manifest named a newer Bun, a higher oldest safe version, or both.
    /// For the host supervisor to download the new Bun and move the host, or to take the security path.
    /// </summary>
    event EventHandler<BunReleaseChangedEventArgs>? Changed;

    /// <summary>
    /// Reads the manifest now rather than waiting for the schedule, e.g. when the user turns Mods on. Never throws for
    /// a manifest that can't be read: <see cref="Current"/> stays as it was.
    /// </summary>
    Task RefreshAsync(CancellationToken ct);
}

/// <summary>How the release Fleet wants changed.</summary>
public sealed class BunReleaseChangedEventArgs(BunRelease previous, BunRelease current) : EventArgs
{
    /// <summary>The release before.</summary>
    public BunRelease Previous { get; } = previous;

    /// <summary>The release now.</summary>
    public BunRelease Current { get; } = current;

    /// <summary>The recommended Bun is newer than before.</summary>
    public bool NewerVersion { get; } = BunVersion.Parse(current.Version) > BunVersion.Parse(previous.Version);

    /// <summary>The oldest safe version went up: a Bun below it now needs the security path.</summary>
    public bool SaferRequired { get; } = BunVersion.Parse(current.OldestSafe) > BunVersion.Parse(previous.OldestSafe);
}

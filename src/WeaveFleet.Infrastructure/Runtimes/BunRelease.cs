using System.Runtime.InteropServices;

namespace WeaveFleet.Infrastructure.Runtimes;

/// <summary>One platform's Bun archive in a release.</summary>
/// <param name="Rid">The .NET runtime identifier it runs on, e.g. <c>linux-x64</c>.</param>
/// <param name="FileName">The release asset, e.g. <c>bun-linux-x64-baseline.zip</c>. The archive holds one folder
/// named after it (without <c>.zip</c>) with <c>bun</c> or <c>bun.exe</c> inside.</param>
/// <param name="Sha256">The asset's sha256 in lowercase hex, from the release's <c>SHASUMS256.txt</c>.</param>
internal sealed record BunAsset(string Rid, string FileName, string Sha256)
{
    /// <summary>The folder inside the archive that holds the executable.</summary>
    public string Folder => Path.GetFileNameWithoutExtension(FileName);

    /// <summary>The executable's file name on this asset's platform.</summary>
    public string ExecutableName => Rid.StartsWith("win-", StringComparison.Ordinal) ? "bun.exe" : "bun";
}

/// <summary>A Bun release Fleet can install, with the archive and sha256 for each platform it supports.</summary>
internal sealed record BunRelease(string Version, IReadOnlyList<BunAsset> Assets)
{
    /// <summary>Where Bun's releases are downloaded from: <c>{base}/bun-v{version}/{asset}</c>.</summary>
    public static readonly Uri GitHubDownloads = new("https://github.com/oven-sh/bun/releases/download/");

    /// <summary>
    /// The Bun Fleet installs. The x64 builds are the <c>-baseline</c> ones, which run on CPUs without AVX2 (older
    /// machines, and x64 under Rosetta or some VMs). The sha256 values are from the release's <c>SHASUMS256.txt</c>,
    /// which <c>BunReleaseTests</c> checks them against.
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

using Shouldly;
using WeaveFleet.Application.Runtimes;

namespace WeaveFleet.Application.Tests.Runtimes;

/// <summary>
/// Checks the repository's own <c>bun/bun.json</c>. CI's publish workflow runs this class by name before it publishes
/// the file, so keep the name.
/// </summary>
public sealed class BunManifestFileTests
{
    private static string ManifestPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WeaveFleet.slnx")))
            directory = directory.Parent;

        directory.ShouldNotBeNull("WeaveFleet.slnx wasn't found above the test folder.");
        return Path.Combine(directory.FullName, "bun", "bun.json");
    }

    [Fact]
    public void The_repository_manifest_parses()
    {
        BunManifest.TryParse(File.ReadAllBytes(ManifestPath()), out var release, out var error).ShouldBeTrue(error);

        release.ShouldNotBeNull();
    }

    [Fact]
    public void The_repository_manifest_is_the_pinned_release()
    {
        BunManifest.TryParse(File.ReadAllBytes(ManifestPath()), out var release, out var error).ShouldBeTrue(error);

        release!.Version.ShouldBe(BunRelease.Pinned.Version);
        release.OldestSafe.ShouldBe(BunRelease.Pinned.OldestSafe);
        release.Note.ShouldBe(BunRelease.Pinned.Note);
        release.Assets.ShouldBe(BunRelease.Pinned.Assets);
    }

    [Fact]
    public void The_pinned_release_is_still_1_4_2_with_the_same_shas_and_sizes()
    {
        BunRelease.Pinned.Version.ShouldBe("1.4.2");
        BunRelease.Pinned.Assets.ToDictionary(asset => asset.Rid, asset => (asset.Sha256, asset.Size)).ShouldBe(new Dictionary<string, (string, long?)>
        {
            ["linux-x64"] = ("c678040f14fe0440eb839d37cbd0ce4c051a32da72806ac97de6a6aab6bf728f", 36646949),
            ["linux-arm64"] = ("54328bbc2d9c8e0c9f892c544d66c57a83b84139e34909e5ee81758f1ac8fda7", 36602920),
            ["osx-x64"] = ("bad5bbd6cf14d0980d115f5954c9ff904df619d5e994d2da1ffccd3f316300b0", 28440543),
            ["osx-arm64"] = ("90987a3a16d7db556d886ac3d551e7b6d3edf0a1cf43acaed622e8676be1d12f", 25377591),
            ["win-x64"] = ("78c221c2376f79731ccf4e4af0b3bb46d81fefa3296c5abee09ad8a1b21e68c6", 39807490),
            ["win-arm64"] = ("a7a16b876a305fd1029c66dbd27007b4f6112ae896532f675878731a21e50cfd", 36330062),
        });
    }
}

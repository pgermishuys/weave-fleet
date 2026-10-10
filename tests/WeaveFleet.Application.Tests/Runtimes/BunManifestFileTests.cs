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

    // The sha256 values are checked against the release's verified SHASUMS256.txt in the Infrastructure tests
    // (BunReleaseTests). Nothing here names a version, so the bump job's PRs pass without editing tests.
    [Fact]
    public void Every_pinned_asset_has_a_plausible_size()
    {
        BunRelease.Pinned.Assets.Select(asset => asset.Rid).ShouldBe(BunManifest.AssetNames.Keys);
        BunRelease.Pinned.Assets.ShouldAllBe(asset => asset.Size > 1024 * 1024);
    }
}

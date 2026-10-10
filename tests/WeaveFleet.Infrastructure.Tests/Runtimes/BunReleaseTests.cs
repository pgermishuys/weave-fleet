using WeaveFleet.Application.Runtimes;

namespace WeaveFleet.Infrastructure.Tests.Runtimes;

public sealed class BunReleaseTests
{
    [Fact]
    public void Every_pinned_sha256_is_the_one_in_the_release_shasums()
    {
        var release = BunRelease.Pinned;
        var shasums = ReadShasums(release.Version);

        foreach (var asset in release.Assets)
        {
            shasums.ShouldContainKey(asset.FileName);
            asset.Sha256.ShouldBe(shasums[asset.FileName], asset.Rid);
        }
    }

    [Theory]
    [InlineData("linux-x64", "bun-linux-x64-baseline.zip", "bun")]
    [InlineData("linux-arm64", "bun-linux-aarch64.zip", "bun")]
    [InlineData("osx-x64", "bun-darwin-x64-baseline.zip", "bun")]
    [InlineData("osx-arm64", "bun-darwin-aarch64.zip", "bun")]
    [InlineData("win-x64", "bun-windows-x64-baseline.zip", "bun.exe")]
    [InlineData("win-arm64", "bun-windows-aarch64.zip", "bun.exe")]
    public void Each_platform_has_its_asset(string rid, string fileName, string executable)
    {
        var asset = BunRelease.Pinned.AssetFor(rid);

        asset.ShouldNotBeNull();
        asset.FileName.ShouldBe(fileName);
        asset.Folder.ShouldBe(Path.GetFileNameWithoutExtension(fileName));
        asset.ExecutableName.ShouldBe(executable);
    }

    [Theory]
    [InlineData("linux-x64", 36646949L)]
    [InlineData("linux-arm64", 36602920L)]
    [InlineData("osx-x64", 28440543L)]
    [InlineData("osx-arm64", 25377591L)]
    [InlineData("win-x64", 39807490L)]
    [InlineData("win-arm64", 36330062L)]
    public void Each_pinned_asset_knows_its_size(string rid, long size)
        => BunRelease.Pinned.AssetFor(rid)!.Size.ShouldBe(size);

    [Fact]
    public void The_pinned_source_never_announces_a_new_release()
    {
        var raised = 0;
        var releases = new PinnedBunReleases();
        releases.Changed += (_, _) => raised++;

        releases.Current.ShouldBe(BunRelease.Pinned);
        raised.ShouldBe(0);
    }

    [Fact]
    public void A_platform_without_a_build_has_no_asset()
        => BunRelease.Pinned.AssetFor("linux-musl-x64").ShouldBeNull();

    [Fact]
    public void Downloads_come_from_the_release_tag()
    {
        var release = BunRelease.Pinned;
        var asset = release.AssetFor("linux-x64")!;

        release.DownloadUrl(BunRelease.GitHubDownloads, asset).ToString()
            .ShouldBe($"https://github.com/oven-sh/bun/releases/download/bun-v{release.Version}/bun-linux-x64-baseline.zip");
    }

    [Fact]
    public void The_current_platform_is_named_as_dotnet_names_it()
        => BunRelease.CurrentRid().ShouldMatch("^(linux|osx|win)-(x64|arm64|[a-z0-9]+)$");

    private static Dictionary<string, string> ReadShasums(string version)
        => File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Bun", $"bun-v{version}-SHASUMS256.txt"))
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[1], parts => parts[0], StringComparer.Ordinal);
}

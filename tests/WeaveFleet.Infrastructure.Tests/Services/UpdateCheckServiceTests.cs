using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Updates;
using WeaveFleet.Infrastructure.Updates;

namespace WeaveFleet.Infrastructure.Tests.Services;

public sealed class UpdateCheckServiceTests
{
    // ── IsNewerVersion ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("0.2.0", "0.1.9", true)]
    [InlineData("1.0.0", "0.9.9", true)]
    [InlineData("0.1.10", "0.1.9", true)]
    public void is_newer_version_returns_true_when_latest_is_newer(string latest, string current, bool expected)
    {
        UpdateCheckService.IsNewerVersion(latest, current).ShouldBe(expected);
    }

    [Theory]
    [InlineData("0.1.9", "0.1.9", false)]
    [InlineData("0.1.8", "0.1.9", false)]
    [InlineData("0.9.0", "1.0.0", false)]
    public void is_newer_version_returns_false_when_up_to_date_or_older(string latest, string current, bool expected)
    {
        UpdateCheckService.IsNewerVersion(latest, current).ShouldBe(expected);
    }

    // A build from source is stamped by MinVer with a prerelease, e.g. 0.62.1-alpha.0.4 four commits past v0.62.0.
    [Theory]
    [InlineData("0.62.0", "0.62.1-alpha.0.4", false)]
    [InlineData("0.62.1", "0.62.1-alpha.0.4", true)]
    [InlineData("0.62.0", "0.62.0-alpha", true)]
    [InlineData("0.62.0-alpha", "0.62.0", false)]
    [InlineData("0.62.1-alpha.0.4", "0.62.0", true)]
    [InlineData("0.62.1-alpha.0.5", "0.62.1-alpha.0.4", true)]
    [InlineData("0.62.1-alpha.0.10", "0.62.1-alpha.0.9", true)]
    [InlineData("0.62.1-alpha.0.4", "0.62.1-alpha.0.4", false)]
    [InlineData("0.62.1-beta", "0.62.1-alpha.0.4", true)]
    [InlineData("0.62.1-alpha.1", "0.62.1-alpha", true)]
    [InlineData("0.62.1-alpha.beta", "0.62.1-alpha.1", true)]
    [InlineData("0.62.0", "0.0.0-alpha.0", true)]
    public void is_newer_version_understands_prerelease_versions(string latest, string current, bool expected)
    {
        UpdateCheckService.IsNewerVersion(latest, current).ShouldBe(expected);
    }

    [Theory]
    [InlineData("v0.62.0", "0.62.1-alpha.0.4+d311447a", false)]
    [InlineData("0.62.1+abc", "v0.62.1-alpha.0.4+d311447a", true)]
    [InlineData("0.62.0+abc", "0.62.0+def", false)]
    [InlineData("0.62", "0.62.0", false)]
    [InlineData("0.62.0", "0.62", false)]
    public void is_newer_version_ignores_build_metadata_a_v_prefix_and_missing_zeros(string latest, string current, bool expected)
    {
        UpdateCheckService.IsNewerVersion(latest, current).ShouldBe(expected);
    }

    // What can't be read is never an update: a guess either way could fetch an older Fleet.
    [Theory]
    [InlineData("not-a-version", "0.1.9")]
    [InlineData("0.2.0", "not-a-version")]
    [InlineData("0.2.0-", "0.1.9")]
    [InlineData("0.2.0-alpha..1", "0.1.9")]
    [InlineData("0.2.0-alpha", "0.2.0-")]
    public void is_newer_version_is_false_when_either_version_cannot_be_read(string latest, string current)
    {
        UpdateCheckService.IsNewerVersion(latest, current).ShouldBeFalse();
    }

    [Theory]
    [InlineData("", "0.1.9")]
    [InlineData("not-a-version", "0.1.9")]
    public void is_newer_version_handles_malformed_versions_gracefully(string latest, string current)
    {
        // Just verify it doesn't throw.
        Should.NotThrow(() => UpdateCheckService.IsNewerVersion(latest, current));
    }

    // ── Release feed ───────────────────────────────────────────────────────────

    [Fact]
    public void download_url_points_at_github_unless_a_release_feed_is_set()
    {
        var github = new UpdateOptions { GitHubRepo = "acme/fleet-releases" };
        UpdateCheckService.DownloadUrl(github, "0.2.0", "fleet-v0.2.0-linux-x64.tar.gz")
            .ShouldBe("https://github.com/acme/fleet-releases/releases/download/v0.2.0/fleet-v0.2.0-linux-x64.tar.gz");

        var feed = new UpdateOptions { GitHubRepo = "acme/fleet-releases", ReleasesBaseUrl = "http://127.0.0.1:8099/" };
        UpdateCheckService.DownloadUrl(feed, "0.2.0", "fleet-v0.2.0-linux-x64.tar.gz")
            .ShouldBe("http://127.0.0.1:8099/download/v0.2.0/fleet-v0.2.0-linux-x64.tar.gz");
    }

    [Fact]
    public void the_releases_list_is_read_from_the_release_feed_when_one_is_set()
    {
        var factory = new FeedlessHttpClientFactory();
        using var github = new ReleaseNotesStore(factory, new FleetOptions { Update = new UpdateOptions { GitHubRepo = "acme/fleet-releases" } }, NullLogger<ReleaseNotesStore>.Instance);
        github.ReleasesUrl.ShouldStartWith("https://api.github.com/repos/acme/fleet-releases/releases");

        using var feed = new ReleaseNotesStore(factory, new FleetOptions { Update = new UpdateOptions { ReleasesBaseUrl = "http://127.0.0.1:8099" } }, NullLogger<ReleaseNotesStore>.Instance);
        feed.ReleasesUrl.ShouldBe("http://127.0.0.1:8099/releases");
    }

    private sealed class FeedlessHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("No request is made.");
    }

    // ── GetAssetName ───────────────────────────────────────────────────────────

    [Fact]
    public void get_asset_name_returns_expected_format_for_current_platform()
    {
        var assetName = UpdateCheckService.GetAssetName("0.2.0");

        // Must match the pattern fleet-v{version}-{rid}.{ext}
        assetName.ShouldStartWith("fleet-v0.2.0-");
        assetName.ShouldMatch(@"fleet-v0\.2\.0-(win-x64|win-arm64|linux-x64|osx-arm64)\.(zip|tar\.gz)");
    }

    [Fact]
    public void get_asset_name_uses_zip_on_windows_or_tar_gz_on_unix()
    {
        var assetName = UpdateCheckService.GetAssetName("1.0.0");

        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            assetName.ShouldEndWith(".zip");
        else
            assetName.ShouldEndWith(".tar.gz");
    }

    // ── Desktop mode ──────────────────────────────────────────────────────────

    [Fact]
    public async Task a_fleet_the_desktop_app_started_reports_updates_as_managed()
    {
        var (service, stateHolder) = CreateDesktopService();

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!;

        stateHolder.State.Status.ShouldBe(UpdateStatus.Managed);
    }

    [Fact]
    public async Task a_fleet_the_desktop_app_started_never_checks_github()
    {
        var (service, stateHolder) = CreateDesktopService();

        // The factory throws if anything asks it for a client.
        await service.CheckForUpdateAsync(CancellationToken.None);

        stateHolder.State.ShouldBe(UpdateState.Initial);
    }

    private static (UpdateCheckService Service, UpdateStateHolder StateHolder) CreateDesktopService()
    {
        var factory = new ThrowingHttpClientFactory();
        var stateHolder = new UpdateStateHolder();
        var options = new FleetOptions { Desktop = new DesktopOptions { Enabled = true } };
        var download = new UpdateDownloadService(factory, stateHolder, NullLogger<UpdateDownloadService>.Instance);
        var notes = new ReleaseNotesStore(factory, options, NullLogger<ReleaseNotesStore>.Instance);
        return (new UpdateCheckService(factory, options, stateHolder, download, notes, NullLogger<UpdateCheckService>.Instance), stateHolder);
    }

    private sealed class ThrowingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("No HTTP in desktop mode.");
    }

    // ── GetRuntimeIdentifier ──────────────────────────────────────────────────

    [Fact]
    public void get_runtime_identifier_returns_known_rid()
    {
        var rid = UpdateCheckService.GetRuntimeIdentifier();
        var knownRids = new[] { "win-x64", "win-arm64", "linux-x64", "osx-arm64" };
        knownRids.ShouldContain(rid);
    }
}

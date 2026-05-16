using WeaveFleet.Infrastructure.Services;

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

    [Theory]
    [InlineData("", "0.1.9")]
    [InlineData("not-a-version", "0.1.9")]
    public void is_newer_version_handles_malformed_versions_gracefully(string latest, string current)
    {
        // Just verify it doesn't throw.
        Should.NotThrow(() => UpdateCheckService.IsNewerVersion(latest, current));
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

    // ── GetRuntimeIdentifier ──────────────────────────────────────────────────

    [Fact]
    public void get_runtime_identifier_returns_known_rid()
    {
        var rid = UpdateCheckService.GetRuntimeIdentifier();
        var knownRids = new[] { "win-x64", "win-arm64", "linux-x64", "osx-arm64" };
        knownRids.ShouldContain(rid);
    }
}

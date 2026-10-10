using WeaveFleet.Application.Runtimes;
using WeaveFleet.Infrastructure.Runtimes;

namespace WeaveFleet.Infrastructure.Tests.Runtimes;

/// <summary>
/// Probes a real Bun, so a real start with the cleared environment is proven where the script-based tests can't run:
/// on Windows, bun.exe has to start with nothing but <c>SystemRoot</c> and <c>windir</c>. CI's Windows job installs
/// Bun and passes its path in <c>FLEET_TEST_REAL_BUN</c>; without it these tests do nothing.
/// </summary>
[Trait("Category", "ModsFileSafety")]
public sealed class BunRealProbeTests
{
    private static string? RealBun =>
        Environment.GetEnvironmentVariable("FLEET_TEST_REAL_BUN") is { Length: > 0 } path && File.Exists(path) ? path : null;

    [Fact]
    public async Task A_real_bun_reports_its_version_through_the_cleared_environment_start()
    {
        if (RealBun is not { } bun)
            return;

        var result = await BunVersionProbe.RunAsync(bun, TimeSpan.FromSeconds(30), CancellationToken.None);

        result.Error.ShouldBeNull();
        result.Version.ShouldNotBeNull();
        result.Version.Value.ShouldBeGreaterThanOrEqualTo(BunVersion.Parse(BunRelease.MinimumVersion));
    }

    [Fact]
    public async Task A_real_bun_is_usable_when_checked_by_its_full_path()
    {
        if (RealBun is not { } bun)
            return;

        var candidate = await new BunMachineFinder { Home = Path.GetTempPath() }.CheckAsync(Path.GetFullPath(bun), CancellationToken.None);

        candidate.Status.ShouldBe(BunCandidateStatuses.Usable, candidate.Message);
        candidate.Version.ShouldNotBeNull();
    }
}

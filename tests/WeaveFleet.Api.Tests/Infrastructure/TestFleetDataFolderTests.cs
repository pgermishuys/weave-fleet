using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Configuration;

namespace WeaveFleet.Api.Tests.Infrastructure;

public sealed class TestFleetDataFolderTests
{
    /// <summary>
    /// The test Fleets ran side by side with their databases all in the temp folder, so they shared its "screenshots"
    /// folder too: one starting up cleared a session folder another had just made for its screenshot, and that
    /// screenshot came back with no id.
    /// </summary>
    [Fact]
    public void Each_test_Fleet_keeps_its_screenshots_and_pages_in_a_folder_of_its_own()
    {
        using var one = new ApiWebApplicationFactory(authEnabled: false);
        using var other = new ApiWebApplicationFactory(authEnabled: false);

        var mine = one.Services.GetRequiredService<FleetOptions>();
        var theirs = other.Services.GetRequiredService<FleetOptions>();

        mine.ResolvedScreenshotDirectory.ShouldNotBe(theirs.ResolvedScreenshotDirectory);
        mine.ResolvedPageDirectory.ShouldNotBe(theirs.ResolvedPageDirectory);
    }
}

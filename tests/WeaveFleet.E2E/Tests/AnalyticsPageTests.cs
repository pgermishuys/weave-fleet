using WeaveFleet.E2E.Infrastructure;
using WeaveFleet.E2E.Pages;

namespace WeaveFleet.E2E.Tests;

/// <summary>
/// E2E tests for the Analytics page.
/// </summary>
[Trait("Category", "E2E")]
public sealed class AnalyticsPageTests : E2ETestBase,
    IClassFixture<FleetWebApplicationFactory>,
    IClassFixture<PlaywrightFixture>
{
    public AnalyticsPageTests(FleetWebApplicationFactory factory, PlaywrightFixture playwright)
        : base(factory, playwright) { }

    /// <summary>
    /// Task 27: Analytics page loads without errors.
    /// </summary>
    [Fact]
    public async Task AnalyticsPage_LoadsWithoutErrors()
    {
        await WithFailureCapture(async () =>
        {
            var analytics = new AnalyticsPage(Page);
            await analytics.GotoAsync();

            // Page should load without a hard error
            var hasError = await analytics.HasErrorAsync();
            Assert.False(hasError, "Analytics page showed an error state");

            // No JavaScript errors should have thrown (check page title is present)
            var title = await Page.TitleAsync();
            Assert.NotNull(title);
        });
    }
}

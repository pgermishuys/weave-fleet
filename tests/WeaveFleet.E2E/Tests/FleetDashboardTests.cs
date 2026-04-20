using WeaveFleet.E2E.Infrastructure;
using WeaveFleet.E2E.Pages;

namespace WeaveFleet.E2E.Tests;

/// <summary>
/// E2E tests for the Fleet Dashboard page.
/// </summary>
[Trait("Category", "E2E")]
[Trait("Lane", "Workflow")]
public sealed class FleetDashboardTests : E2ETestBase,
    IClassFixture<PlaywrightFixture>
{
    private readonly FleetWebApplicationFactory _factory;

    public FleetDashboardTests(PlaywrightFixture playwright)
        : this(new FleetWebApplicationFactory(), playwright)
    {
    }

    private FleetDashboardTests(FleetWebApplicationFactory factory, PlaywrightFixture playwright)
        : base(factory, playwright)
    {
        _factory = factory;
    }

    public override async Task DisposeAsync()
    {
        try
        {
            await base.DisposeAsync();
        }
        finally
        {
            await _factory.DisposeAsync();
        }
    }

    /// <summary>
    /// Task 14: Dashboard loads successfully with no sessions and shows the empty state and summary bar.
    /// Uses a dedicated app factory per test so state is isolated from other dashboard tests.
    /// </summary>
    [Fact]
    public async Task Dashboard_WithNoSessions_ShowsEmptyStateAndSummaryBar()
    {
        await WithFailureCapture(async () =>
        {
            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();

            // Empty state should be visible
            await dashboard.WaitForEmptyStateAsync();

            // Summary bar should be visible
            await dashboard.WaitForSummaryBarAsync();
            var summaryBar = dashboard.GetSummaryBar();
            await Microsoft.Playwright.Assertions.Expect(summaryBar).ToBeVisibleAsync();

            // No session cards should exist
            var cards = await dashboard.GetSessionCardsAsync();
            cards.ShouldBeEmpty();
        });
    }

    /// <summary>
    /// Task 15: Dashboard shows session cards when sessions exist.
    /// We create a session via the API directly (via the page flow) and verify the card appears.
    /// </summary>
    [Fact]
    public async Task Dashboard_AfterSessionCreated_ShowsSessionCard()
    {
        await WithFailureCapture(async () =>
        {
            ConfigureScenario(b =>
                b.WithSimpleTextResponse("_placeholder_", "msg-dash-1", "Dashboard test response"));

            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();

            // Create a session
            var dialog = await dashboard.ClickNewSessionAsync();
            await dialog.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
            await dialog.SubmitAsync();

            // Navigate back to dashboard
            await Page.GotoAsync("/");

            // Wait for at least one session card to appear (API poll may take a moment)
            await dashboard.WaitForSessionCountAsync(1);

            // The session card should now appear
            var cards = await dashboard.GetSessionCardsAsync();
            cards.Count.ShouldBeGreaterThanOrEqualTo(1, $"Expected at least 1 session card, got {cards.Count}");
        });
    }

}

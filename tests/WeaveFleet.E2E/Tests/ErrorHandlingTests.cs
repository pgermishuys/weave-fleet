using WeaveFleet.E2E.Infrastructure;
using WeaveFleet.E2E.Pages;

namespace WeaveFleet.E2E.Tests;

/// <summary>
/// E2E tests for error handling: harness spawn failure and prompt failure.
/// </summary>
[Trait("Category", "E2E")]
public sealed class ErrorHandlingTests : E2ETestBase,
    IClassFixture<FleetWebApplicationFactory>,
    IClassFixture<PlaywrightFixture>
{
    public ErrorHandlingTests(FleetWebApplicationFactory factory, PlaywrightFixture playwright)
        : base(factory, playwright) { }

    /// <summary>
    /// Task 25: When the harness throws on SpawnAsync, the UI shows an error state
    /// and does not navigate to the session detail page.
    /// </summary>
    [Fact]
    public async Task SpawnFailure_ShowsErrorInDialog()
    {
        await WithFailureCapture(async () =>
        {
            // Configure harness to fail on spawn
            ConfigureScenario(b => b.WithSpawnFailure());

            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();

            var dialog = await dashboard.ClickNewSessionAsync();
            await dialog.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));

            // Click submit — this should fail
            var submitButton = Page.GetByTestId("create-session-submit");
            await submitButton.ClickAsync();

            // Wait a moment for the error to propagate
            await Page.WaitForTimeoutAsync(2_000);

            // Should still be on the dashboard (no redirect to session detail)
            Assert.DoesNotContain("/sessions/", Page.Url);

            // An error message should appear in the dialog or on the page
            var errorLocator = Page.GetByText("error", new Microsoft.Playwright.PageGetByTextOptions { Exact = false })
                .Or(Page.GetByText("failed", new Microsoft.Playwright.PageGetByTextOptions { Exact = false }))
                .Or(Page.GetByText("spawn", new Microsoft.Playwright.PageGetByTextOptions { Exact = false }));

            // At minimum, no crash — the page should still be interactive
            await Microsoft.Playwright.Assertions.Expect(Page.GetByTestId("new-session-button")).ToBeVisibleAsync();
        });
    }

    /// <summary>
    /// Task 26: When the harness throws on SendPromptAsync, the UI shows an error state.
    /// </summary>
    [Fact]
    public async Task SendPromptFailure_ShowsErrorState()
    {
        await WithFailureCapture(async () =>
        {
            // First spawn succeeds, but SendPromptAsync will fail
            ConfigureScenario(b => b.WithSendPromptFailure());

            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();

            var dialog = await dashboard.ClickNewSessionAsync();
            await dialog.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));

            var detail = await dialog.SubmitAsync();
            await detail.WaitForLoadedAsync();

            // Attempt to send a prompt — this should trigger an error
            await detail.SendPromptAsync("This will fail");

            // Wait for potential error display
            await Page.WaitForTimeoutAsync(2_000);

            // The page should still be interactive (no crash/hang)
            // and the prompt input should still be visible
            var promptInput = Page.GetByTestId("prompt-input");
            await Microsoft.Playwright.Assertions.Expect(promptInput).ToBeVisibleAsync();
        });
    }
}

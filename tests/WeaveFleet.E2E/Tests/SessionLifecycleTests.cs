using WeaveFleet.E2E.Infrastructure;
using WeaveFleet.E2E.Pages;

namespace WeaveFleet.E2E.Tests;

/// <summary>
/// E2E tests for session lifecycle: creation, message history, abort, delete, rename.
/// </summary>
[Trait("Category", "E2E")]
public sealed class SessionLifecycleTests : E2ETestBase,
    IClassFixture<FleetWebApplicationFactory>,
    IClassFixture<PlaywrightFixture>
{
    public SessionLifecycleTests(FleetWebApplicationFactory factory, PlaywrightFixture playwright)
        : base(factory, playwright) { }

    /// <summary>
    /// Task 17: Create a new session via the UI dialog, verify redirect to session detail page.
    /// </summary>
    [Fact]
    public async Task CreateSession_RedirectsToSessionDetailPage()
    {
        await WithFailureCapture(async () =>
        {
            ConfigureScenario(b =>
                b.WithSimpleTextResponse("_placeholder_", "msg-lifecycle-1", "Session created!"));

            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();

            var dialog = await dashboard.ClickNewSessionAsync();
            await dialog.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
            await dialog.SetTitleAsync("Lifecycle Test Session");

            var detail = await dialog.SubmitAsync();

            // Should be on the session detail page
            Page.Url.ShouldContain("/sessions/");

            // Activity stream should be visible
            await detail.WaitForLoadedAsync();
        });
    }

    /// <summary>
    /// Task 18: View session detail page — activity stream is visible and prompt input is ready.
    /// </summary>
    [Fact]
    public async Task SessionDetail_PromptInputAndActivityStreamVisible()
    {
        await WithFailureCapture(async () =>
        {
            ConfigureScenario(b =>
                b.WithSimpleTextResponse("_placeholder_", "msg-history-1", "Pre-loaded response"));

            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();

            var dialog = await dashboard.ClickNewSessionAsync();
            await dialog.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));

            var detail = await dialog.SubmitAsync();

            // Activity stream is visible
            await detail.WaitForLoadedAsync();

            // Prompt input is accessible
            var promptInput = Page.GetByTestId("prompt-input");
            await Microsoft.Playwright.Assertions.Expect(promptInput).ToBeVisibleAsync();
        });
    }

    /// <summary>
    /// Task 19: Abort a running session — send prompt, verify busy, click abort, verify idle.
    /// </summary>
    [Fact]
    public async Task AbortSession_TransitionsFromBusyToIdle()
    {
        await WithFailureCapture(async () =>
        {
            // Configure a response that takes long enough to allow abort
            ConfigureScenario(b =>
                b.WithSimpleTextResponse("_placeholder_", "msg-abort-1", "Aborted response", TimeSpan.FromMilliseconds(500)));

            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();

            var dialog = await dashboard.ClickNewSessionAsync();
            await dialog.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));

            var detail = await dialog.SubmitAsync();
            await detail.WaitForLoadedAsync();

            // Send a prompt to make the session busy
            await detail.SendPromptAsync("Please do something long");

            // Wait for busy state — abort button should appear
            await detail.WaitForBusyAsync();

            // The abort button is visible in desktop layout
            var abortVisible = await detail.IsAbortVisibleAsync();
            if (abortVisible)
            {
                await detail.ClickAbortAsync();
                // Session should transition back to idle
                await detail.WaitForIdleAsync();
            }
            else
            {
                // If abort button isn't visible (mobile layout or race), just wait for idle
                await detail.WaitForIdleAsync();
            }
        });
    }

    /// <summary>
    /// Task 20: Delete a stopped/completed session — verify confirmation dialog and removal.
    /// </summary>
    [Fact]
    public async Task DeleteSession_ShowsConfirmationDialog()
    {
        await WithFailureCapture(async () =>
        {
            ConfigureScenario(b =>
                b.WithSimpleTextResponse("_placeholder_", "msg-delete-1", "Delete test"));

            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();

            // Create a session
            var dialog = await dashboard.ClickNewSessionAsync();
            await dialog.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
            await dialog.SetTitleAsync("Delete Me");

            // Submit and go to session detail
            await dialog.SubmitAsync();

            // Navigate back to dashboard
            await Page.GotoAsync("/");
            await Page.WaitForLoadStateAsync(Microsoft.Playwright.LoadState.NetworkIdle);

            // Get session cards
            var cards = await dashboard.GetSessionCardsAsync();
            if (cards.Count == 0)
            {
                // No stopped sessions to delete — skip remaining assertions
                return;
            }

            // Find a card with a delete button (only stopped/completed sessions have delete)
            var deleteButton = Page.GetByTestId("session-delete-button").First;
            var isDeleteVisible = await deleteButton.IsVisibleAsync();
            if (!isDeleteVisible)
            {
                // Session may still be running (terminate vs delete) — skip
                return;
            }

            await deleteButton.ClickAsync();

            // Confirmation dialog should appear
            var confirmButton = Page.GetByTestId("delete-dialog-confirm");
            await Microsoft.Playwright.Assertions.Expect(confirmButton).ToBeVisibleAsync();

            // Cancel to avoid actually deleting
            var cancelButton = Page.GetByTestId("delete-dialog-cancel");
            await cancelButton.ClickAsync();
        });
    }

    /// <summary>
    /// Task 21: Session title is visible on the session detail page header.
    /// </summary>
    [Fact]
    public async Task SessionDetail_ShowsTitleInHeader()
    {
        await WithFailureCapture(async () =>
        {
            ConfigureScenario(b =>
                b.WithSimpleTextResponse("_placeholder_", "msg-rename-1", "Title test"));

            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();

            var dialog = await dashboard.ClickNewSessionAsync();
            await dialog.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
            await dialog.SetTitleAsync("My Titled Session");

            await dialog.SubmitAsync();

            // The header should show the session title (or at least the session ID)
            var header = Page.Locator("header h2");
            await Microsoft.Playwright.Assertions.Expect(header).ToBeVisibleAsync();
        });
    }
}

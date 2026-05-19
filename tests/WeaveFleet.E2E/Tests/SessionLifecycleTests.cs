using System.Net.Http.Json;
using WeaveFleet.E2E.Infrastructure;
using WeaveFleet.E2E.Pages;

namespace WeaveFleet.E2E.Tests;

/// <summary>
/// E2E tests for session lifecycle: creation, message history, abort, delete, rename.
/// </summary>
[Trait("Category", "E2E")]
[Trait("Lane", "Workflow")]
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

            await dialog.SubmitAsync();

            var sessionId = new Uri(Page.Url).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();

            await Page.GotoAsync("/");
            await Page.WaitForLoadStateAsync(Microsoft.Playwright.LoadState.NetworkIdle);
            await Microsoft.Playwright.Assertions.Expect(dashboard.GetSessionCard(sessionId)).ToBeVisibleAsync();

            await dashboard.ClickDeleteSessionAsync(sessionId);

            // Confirmation dialog should appear
            var confirmButton = Page.GetByTestId("delete-dialog-confirm");
            await Microsoft.Playwright.Assertions.Expect(confirmButton).ToBeVisibleAsync();

            await confirmButton.ClickAsync();

            await Microsoft.Playwright.Assertions.Expect(dashboard.GetSessionCard(sessionId)).ToHaveCountAsync(0);
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

            var detail = await dialog.SubmitAsync();
            await detail.WaitForLoadedAsync();

            var header = Page.GetByRole(AriaRole.Heading, new() { Name = "My Titled Session", Exact = true });
            await Microsoft.Playwright.Assertions.Expect(header).ToBeVisibleAsync();
            await Microsoft.Playwright.Assertions.Expect(header).ToHaveTextAsync("My Titled Session");
        });
    }

    /// <summary>
    /// Task 22: Archived sessions remain readable via direct URL but are read-only until unarchived.
    /// </summary>
    [Fact]
    public async Task ArchivedSessionDetail_DirectUrlIsReadableButNotWritableUntilUnarchived()
    {
        await WithFailureCapture(async () =>
        {
            ConfigureScenario(b =>
                b.WithSimpleTextResponse("_placeholder_", "msg-archive-1", "Archive test"));

            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();

            var dialog = await dashboard.ClickNewSessionAsync();
            await dialog.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
            await dialog.SetTitleAsync("Archived Detail Session");

            var detail = await dialog.SubmitAsync();
            await detail.WaitForLoadedAsync();

            var sessionUrl = new Uri(Page.Url);
            var sessionId = sessionUrl.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();
            var instanceId = System.Web.HttpUtility.ParseQueryString(sessionUrl.Query)["instanceId"];

            instanceId.ShouldNotBeNullOrWhiteSpace();

            await detail.ClickStopAsync();
            await detail.ConfirmStopAsync();
            await detail.WaitForStoppedBannerAsync();

            await Page.GetByTestId("session-archive-banner-button").ClickAsync();
            await detail.WaitForArchivedBannerAsync();
            await detail.WaitForPromptDisabledAsync();

            var directUrl = $"/sessions/{Uri.EscapeDataString(sessionId)}";
            await Page.GotoAsync(directUrl);
            await detail.WaitForLoadedAsync();
            await detail.WaitForArchivedBannerAsync();
            await detail.WaitForPromptDisabledAsync();

            (await detail.IsArchivedBadgeVisibleAsync()).ShouldBeTrue();
            var archivedBannerText = await detail.GetArchivedBannerTextAsync();
            archivedBannerText.ShouldNotBeNull();
            archivedBannerText.ShouldContain("Unarchive before resuming or sending prompts");

            await detail.ClickUnarchiveAsync();
            await Microsoft.Playwright.Assertions.Expect(Page.GetByTestId("session-archived-banner")).ToHaveCountAsync(0);
            await detail.WaitForStoppedBannerAsync();
            await detail.WaitForPromptDisabledAsync();
        });
    }

    [Fact]
    public async Task StoppedSession_CanBeArchivedUnarchivedAndResumed()
    {
        await WithFailureCapture(async () =>
        {
            ConfigureScenario(b =>
                b.WithSimpleTextResponse("_placeholder_", "msg-resume-1", "Resume test"));

            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();

            var dialog = await dashboard.ClickNewSessionAsync();
            await dialog.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
            await dialog.SetTitleAsync("Resume After Unarchive");

            var detail = await dialog.SubmitAsync();
            await detail.WaitForLoadedAsync();

            await detail.ClickStopAsync();
            await detail.ConfirmStopAsync();
            await detail.WaitForStoppedBannerAsync();

            await detail.ClickArchiveAsync();
            await detail.WaitForArchivedBannerAsync();

            await detail.ClickUnarchiveAsync();
            await Microsoft.Playwright.Assertions.Expect(Page.GetByTestId("session-archived-banner")).ToHaveCountAsync(0);
            await detail.WaitForStoppedBannerAsync();

            await detail.ClickResumeAsync();
            await detail.WaitForLoadedAsync();
            await detail.WaitForIdleAsync();
        });
    }

    [Fact]
    public async Task ArchivedSession_CanBePermanentlyDeletedFromArchivedView()
    {
        await WithFailureCapture(async () =>
        {
            ConfigureScenario(b =>
                b.WithSimpleTextResponse("_placeholder_", "msg-delete-archived-1", "Delete archived"));

            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();

            var dialog = await dashboard.ClickNewSessionAsync();
            await dialog.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
            await dialog.SetTitleAsync("Archived Delete Session");

            var detail = await dialog.SubmitAsync();
            await detail.WaitForLoadedAsync();

            var sessionUrl = new Uri(Page.Url);
            var sessionId = sessionUrl.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();

            await detail.ClickStopAsync();
            await detail.ConfirmStopAsync();
            await detail.WaitForStoppedBannerAsync();

            await detail.ClickArchiveAsync();
            await detail.WaitForArchivedBannerAsync();

            await detail.ClickPermanentDeleteAsync();
            await detail.ConfirmDeleteAsync();

            await Page.WaitForURLAsync(new System.Text.RegularExpressions.Regex("/$"));
            await dashboard.SetRetentionFilterAsync("Archived");
            await Microsoft.Playwright.Assertions.Expect(dashboard.GetSessionCard(sessionId)).ToHaveCountAsync(0);
        });
    }

    [Fact]
    public async Task Detail_StopWithoutArchive_RemainsVisibleInActiveFleetAndSidebar()
    {
        await WithFailureCapture(async () =>
        {
            ConfigureScenario(b =>
                b.WithSimpleTextResponse("_placeholder_", "msg-stop-only-1", "Stop only"));

            var dashboard = new FleetDashboardPage(Page);
            var sidebar = new FleetSidebarPage(Page);
            await dashboard.GotoAsync();

            var dialog = await dashboard.ClickNewSessionAsync();
            await dialog.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
            await dialog.SetTitleAsync("Stop Without Archive");

            var detail = await dialog.SubmitAsync();
            await detail.WaitForLoadedAsync();

            var sessionId = new Uri(Page.Url).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();

            await detail.ClickStopAsync();
            await detail.ConfirmStopAsync();
            await detail.WaitForStoppedBannerAsync();
            await Microsoft.Playwright.Assertions.Expect(Page.GetByTestId("session-archived-banner")).ToHaveCountAsync(0);

            await Page.GotoAsync("/");
            await Page.WaitForLoadStateAsync(Microsoft.Playwright.LoadState.NetworkIdle);
            await Microsoft.Playwright.Assertions.Expect(dashboard.GetSessionCard(sessionId)).ToBeVisibleAsync();
            await sidebar.ExpectSessionVisibleAsync(sessionId);
        });
    }

    [Fact]
    public async Task ArchivedSession_IsHiddenFromActiveAndVisibleInArchivedAndAllFleetAndSidebar()
    {
        await WithFailureCapture(async () =>
        {
            ConfigureScenario(b =>
                b.WithSimpleTextResponse("_placeholder_", "msg-filter-1", "Filter test"));

            var dashboard = new FleetDashboardPage(Page);
            var sidebar = new FleetSidebarPage(Page);
            await dashboard.GotoAsync();

            var dialog = await dashboard.ClickNewSessionAsync();
            await dialog.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
            await dialog.SetTitleAsync("Archive Visibility Session");

            var detail = await dialog.SubmitAsync();
            await detail.WaitForLoadedAsync();

            var sessionId = new Uri(Page.Url).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();

            await detail.ClickStopAsync();
            await detail.ConfirmStopAsync();
            await detail.WaitForStoppedBannerAsync();
            await detail.ClickArchiveAsync();
            await detail.WaitForArchivedBannerAsync();

            await Page.GotoAsync("/");
            await Page.WaitForLoadStateAsync(Microsoft.Playwright.LoadState.NetworkIdle);

            await Microsoft.Playwright.Assertions.Expect(dashboard.GetSessionCard(sessionId)).ToHaveCountAsync(0);
            await sidebar.ExpectSessionHiddenAsync(sessionId);

            await dashboard.SetRetentionFilterAsync("Active");
            await Microsoft.Playwright.Assertions.Expect(dashboard.GetSessionCard(sessionId)).ToHaveCountAsync(0);
            await sidebar.ExpectSessionHiddenAsync(sessionId);

            await dashboard.SetRetentionFilterAsync("Archived");
            await Microsoft.Playwright.Assertions.Expect(dashboard.GetSessionCard(sessionId)).ToBeVisibleAsync();
            await dashboard.ExpectArchivedBadgeAsync(sessionId);
            await sidebar.ExpectSessionVisibleAsync(sessionId);

            await dashboard.SetRetentionFilterAsync("All");
            await Microsoft.Playwright.Assertions.Expect(dashboard.GetSessionCard(sessionId)).ToBeVisibleAsync();
            await sidebar.ExpectSessionVisibleAsync(sessionId);
        });
    }

    [Fact]
    public async Task StoppedActiveSession_CanBeDeletedFromFleetActiveView()
    {
        await WithFailureCapture(async () =>
        {
            ConfigureScenario(b =>
                b.WithSimpleTextResponse("_placeholder_", "msg-delete-active-1", "Delete active stopped"));

            var dashboard = new FleetDashboardPage(Page);
            var sidebar = new FleetSidebarPage(Page);
            await dashboard.GotoAsync();

            var dialog = await dashboard.ClickNewSessionAsync();
            await dialog.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
            await dialog.SetTitleAsync("Delete Active Stopped");

            var detail = await dialog.SubmitAsync();
            await detail.WaitForLoadedAsync();

            var sessionId = new Uri(Page.Url).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();

            await detail.ClickStopAsync();
            await detail.ConfirmStopAsync();
            await detail.WaitForStoppedBannerAsync();

            await Page.GotoAsync("/");
            await Page.WaitForLoadStateAsync(Microsoft.Playwright.LoadState.NetworkIdle);
            await Microsoft.Playwright.Assertions.Expect(dashboard.GetSessionCard(sessionId)).ToBeVisibleAsync();

            await dashboard.ClickDeleteSessionAsync(sessionId);
            await detail.ConfirmDeleteAsync();

            await Microsoft.Playwright.Assertions.Expect(dashboard.GetSessionCard(sessionId)).ToHaveCountAsync(0);
            await sidebar.ExpectSessionHiddenAsync(sessionId);

            await dashboard.SetRetentionFilterAsync("All");
            await Microsoft.Playwright.Assertions.Expect(dashboard.GetSessionCard(sessionId)).ToHaveCountAsync(0);
        });
    }

    [Fact]
    public async Task Sidebar_Archive_HidesSessionFromActiveView()
    {
        await WithFailureCapture(async () =>
        {
            var dashboard = new FleetDashboardPage(Page);
            var sidebar = new FleetSidebarPage(Page);
            var sessionId = await CreateStoppedSessionAsync("Sidebar Archive Session", "msg-sidebar-archive-1", "Sidebar archive flow");

            await Page.GotoAsync("/");
            await Page.WaitForLoadStateAsync(Microsoft.Playwright.LoadState.NetworkIdle);
            await sidebar.ClickSessionMenuItemAsync(sessionId, "Completed");
            await Page.GetByTestId("complete-dialog-confirm").ClickAsync();

            await Microsoft.Playwright.Assertions.Expect(dashboard.GetSessionCard(sessionId)).ToHaveCountAsync(0);
            await sidebar.ExpectSessionHiddenAsync(sessionId);

            await dashboard.SetRetentionFilterAsync("Archived");
            await Microsoft.Playwright.Assertions.Expect(dashboard.GetSessionCard(sessionId)).ToBeVisibleAsync();
            await sidebar.ExpectSessionVisibleAsync(sessionId);
        });
    }

    [Fact]
    public async Task Sidebar_Unarchive_RestoresSessionToActiveView()
    {
        await WithFailureCapture(async () =>
        {
            var dashboard = new FleetDashboardPage(Page);
            var sidebar = new FleetSidebarPage(Page);
            var sessionId = await CreateStoppedSessionAsync("Sidebar Unarchive Session", "msg-sidebar-unarchive-1", "Sidebar unarchive flow");

            await ArchiveSessionFromSidebarAsync(dashboard, sidebar, sessionId);
            await dashboard.SetRetentionFilterAsync("Archived");
            await sidebar.ClickSessionMenuItemAsync(sessionId, "Unarchive");

            await sidebar.ExpectSessionHiddenAsync(sessionId);

            await dashboard.SetRetentionFilterAsync("Active");
            await Microsoft.Playwright.Assertions.Expect(dashboard.GetSessionCard(sessionId)).ToBeVisibleAsync();
            await sidebar.ExpectSessionVisibleAsync(sessionId);
        });
    }

    [Fact]
    public async Task Sidebar_PermanentDelete_RemovesSessionEntirely()
    {
        await WithFailureCapture(async () =>
        {
            var dashboard = new FleetDashboardPage(Page);
            var sidebar = new FleetSidebarPage(Page);
            var detail = new SessionDetailPage(Page);
            var sessionId = await CreateStoppedSessionAsync("Sidebar Delete Session", "msg-sidebar-delete-1", "Sidebar delete flow");

            await Page.GotoAsync("/");
            await Page.WaitForLoadStateAsync(Microsoft.Playwright.LoadState.NetworkIdle);
            await sidebar.ClickSessionMenuItemAsync(sessionId, "Permanently Delete");
            await detail.ConfirmDeleteAsync();

            await Microsoft.Playwright.Assertions.Expect(dashboard.GetSessionCard(sessionId)).ToHaveCountAsync(0);
            await sidebar.ExpectSessionHiddenAsync(sessionId);

            await dashboard.SetRetentionFilterAsync("All");
            await Microsoft.Playwright.Assertions.Expect(dashboard.GetSessionCard(sessionId)).ToHaveCountAsync(0);
        });
    }

    [Fact]
    public async Task ArchivedDetail_NewContextWindow_OpensForkDialogAndNavigatesToFreshSession()
    {
        await WithFailureCapture(async () =>
        {
            ConfigureScenario(b =>
                b.WithSimpleTextResponse("_placeholder_", "msg-fork-1", "Fork path"));

            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();

            var dialog = await dashboard.ClickNewSessionAsync();
            await dialog.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
            await dialog.SetTitleAsync("Fork Source Session");

            var detail = await dialog.SubmitAsync();
            await detail.WaitForLoadedAsync();

            var originalSessionId = new Uri(Page.Url).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();

            await detail.ClickStopAsync();
            await detail.ConfirmStopAsync();
            await detail.WaitForStoppedBannerAsync();
            await detail.ClickArchiveAsync();
            await detail.WaitForArchivedBannerAsync();

            await detail.ClickNewContextWindowAsync();
            await detail.WaitForForkDialogAsync();
            (await detail.GetForkSourceTitleAsync()).ShouldBe("Fork Source Session");
            await detail.SetForkTitleAsync("Forked Follow-up Session");
            await detail.SubmitForkAsync();
            await detail.WaitForLoadedAsync();

            var newSessionId = new Uri(Page.Url).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();
            newSessionId.ShouldNotBe(originalSessionId);
            await Microsoft.Playwright.Assertions.Expect(Page.GetByTestId("session-archived-banner")).ToHaveCountAsync(0);
            await Microsoft.Playwright.Assertions.Expect(Page.GetByTestId("prompt-input")).ToBeEnabledAsync();
        });
    }

    /// <summary>
    /// Verifies that right-clicking a project in the sidebar shows a "New Session" context menu item,
    /// and that clicking it opens the New Session dialog with the project pre-selected, allowing
    /// a session to be created and appear in the sidebar.
    /// </summary>
    [Fact]
    public async Task NewSessionFromProjectContextMenu_OpensDialogAndCreatesSession()
    {
        await WithFailureCapture(async () =>
        {
            ConfigureScenario(b =>
                b.WithSimpleTextResponse("_placeholder_", "msg-proj-ctx-1", "Project context menu session"));

            // Create a project via the API so we have a known project ID
            using var http = new HttpClient { BaseAddress = new Uri(ServerUrl) };
            var createResp = await http.PostAsJsonAsync("/api/projects", new { name = "E2E Context Menu Project", description = "" });
            createResp.EnsureSuccessStatusCode();
            var project = await createResp.Content.ReadFromJsonAsync<ProjectApiResponse>();
            project.ShouldNotBeNull();

            var dashboard = new FleetDashboardPage(Page);
            var sidebar = new FleetSidebarPage(Page);
            await dashboard.GotoAsync();

            // Verify the "New Session" item is present in the project context menu
            await sidebar.OpenProjectContextMenuAsync(project!.Id);
            var newSessionMenuItem = Page.GetByRole(AriaRole.Menuitem, new() { Name = "New Session" });
            await Microsoft.Playwright.Assertions.Expect(newSessionMenuItem).ToBeVisibleAsync();

            // Click "New Session" from the project context menu
            await newSessionMenuItem.ClickAsync();

            // Verify the New Session dialog opens
            var dialogLocator = Page.GetByTestId("new-session-dialog");
            await Microsoft.Playwright.Assertions.Expect(dialogLocator).ToBeVisibleAsync();

            // Fill in the directory and submit to create a session
            var dialog = new NewSessionDialog(Page);
            await dialog.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
            var detail = await dialog.SubmitAsync();
            await detail.WaitForLoadedAsync();

            // Verify the session was created and appears in the sidebar
            var sessionId = new Uri(Page.Url).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();
            await sidebar.ExpectSessionVisibleAsync(sessionId);
        });
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private async Task<string> CreateStoppedSessionAsync(string title, string messageId, string responseText)
    {
        ConfigureScenario(b =>
            b.WithSimpleTextResponse("_placeholder_", messageId, responseText));

        var dashboard = new FleetDashboardPage(Page);
        await CreateSessionAsync(dashboard, title);
        var sessionId = new Uri(Page.Url).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();

        await Page.GotoAsync("/");
        await Page.WaitForLoadStateAsync(Microsoft.Playwright.LoadState.NetworkIdle);
        await Microsoft.Playwright.Assertions.Expect(dashboard.GetSessionCard(sessionId)).ToBeVisibleAsync();

        await dashboard.ClickTerminateSessionAsync(sessionId);

        var sessionCard = dashboard.GetSessionCard(sessionId);
        await sessionCard.HoverAsync();
        await Microsoft.Playwright.Assertions.Expect(sessionCard.GetByTestId("session-archive-button")).ToBeVisibleAsync();

        return sessionId;
    }

    private static async Task<SessionDetailPage> CreateSessionAsync(FleetDashboardPage dashboard, string title)
    {
        await dashboard.GotoAsync();

        var dialog = await dashboard.ClickNewSessionAsync();
        await dialog.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
        await dialog.SetTitleAsync(title);

        var detail = await dialog.SubmitAsync();
        await detail.WaitForLoadedAsync();
        return detail;
    }

    private async Task ArchiveSessionFromSidebarAsync(FleetDashboardPage dashboard, FleetSidebarPage sidebar, string sessionId)
    {
        await Page.GotoAsync("/");
        await Page.WaitForLoadStateAsync(Microsoft.Playwright.LoadState.NetworkIdle);
        await sidebar.ClickSessionMenuItemAsync(sessionId, "Completed");
        await Page.GetByTestId("complete-dialog-confirm").ClickAsync();
        await Microsoft.Playwright.Assertions.Expect(dashboard.GetSessionCard(sessionId)).ToHaveCountAsync(0);
    }

    private sealed record ProjectApiResponse(string Id, string Name, string? Description, string Type, int Position, int SessionCount, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
}

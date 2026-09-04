using Microsoft.Playwright;
using WeaveFleet.E2E.Infrastructure;
using WeaveFleet.E2E.Pages;

namespace WeaveFleet.E2E.Tests;

/// <summary>
/// E2E smoke tests for the content panel (right panel) in the Sessions V2 view.
/// Verifies the two tabs (Files, Changes), reviewed-count label, panel collapse/expand,
/// and roving keyboard focus across tabs.
///
/// Note: the current <c>TestScenarioBuilder</c> has no support for seeding session diffs
/// or mirrored visual artifacts. Content-slot routing to <c>__visual__/plan.md</c> via
/// <c>useContentPanelContext().selectFile</c> (from the artifact chip or a file-browser
/// selection) is covered at the unit level in
/// <c>client/src/composables/__tests__/use-file-browser.test.ts</c> (around line 93); it is
/// not re-verified here because there is no scenario-builder hook to produce a non-empty
/// diff set or a mirrored visual payload for a real session.
/// </summary>
[Trait("Category", "E2E")]
[Trait("Lane", "Smoke")]
public sealed class ContentPanelTests : E2ETestBase,
    IClassFixture<FleetWebApplicationFactory>,
    IClassFixture<PlaywrightFixture>
{
    public ContentPanelTests(FleetWebApplicationFactory factory, PlaywrightFixture playwright)
        : base(factory, playwright) { }

    /// <summary>
    /// Verify that exactly two tabs (Files, Changes) are visible in the content panel,
    /// and that no Preview/Details tabs exist.
    /// </summary>
    [Fact]
    public async Task ContentPanel_ShowsExactlyTwoTabs()
    {
        await WithFailureCapture(async () =>
        {
            ConfigureScenario(b =>
                b.WithSimpleTextResponse(
                    "_placeholder_",
                    "msg-content-panel-1",
                    "Content panel test response"));

            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();

            var dialog = await dashboard.ClickNewSessionAsync();
            await dialog.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));

            var detail = await dialog.SubmitAsync();
            await detail.WaitForLoadedAsync();

            // Files and Changes tabs should both be visible.
            await Assertions.Expect(detail.GetFilesTab()).ToBeVisibleAsync();
            await Assertions.Expect(detail.GetChangesTab()).ToBeVisibleAsync();

            // Exactly two tabs should exist in the tablist — no Preview, no Details.
            await Assertions.Expect(detail.GetAllTabs()).ToHaveCountAsync(2);
        });
    }

    /// <summary>
    /// Verify that clicking the Changes tab switches to it, and that returning to
    /// Files preserves the tab state.
    /// </summary>
    [Fact]
    public async Task ContentPanel_SwitchingBetweenFilesAndChangesPreservesState()
    {
        await WithFailureCapture(async () =>
        {
            ConfigureScenario(b =>
                b.WithSimpleTextResponse(
                    "_placeholder_",
                    "msg-content-panel-2",
                    "Tab state test response"));

            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();

            var dialog = await dashboard.ClickNewSessionAsync();
            await dialog.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));

            var detail = await dialog.SubmitAsync();
            await detail.WaitForLoadedAsync();

            // Start on Files tab.
            await detail.ClickTabAsync("files");
            await Assertions.Expect(detail.GetFilesPanel()).ToBeVisibleAsync();

            var isFilesActive = await detail.IsTabActiveAsync("files");
            isFilesActive.ShouldBeTrue("Files tab should be active after clicking it");

            // Navigate: Files -> Changes -> Files.
            await detail.ClickTabAsync("changes");
            await Assertions.Expect(detail.GetChangesPanel()).ToBeVisibleAsync();

            var isChangesActive = await detail.IsTabActiveAsync("changes");
            isChangesActive.ShouldBeTrue("Changes tab should be active after clicking it");

            await detail.ClickTabAsync("files");
            await Assertions.Expect(detail.GetFilesPanel()).ToBeVisibleAsync();

            var isFilesActiveAgain = await detail.IsTabActiveAsync("files");
            isFilesActiveAgain.ShouldBeTrue("Files tab should be active after returning to it");
        });
    }

    /// <summary>
    /// Verify the "N/M reviewed" label is not rendered on the Changes tab when there are
    /// no changed files (total == 0). The label is only shown when total &gt; 0 per the
    /// RightPanelTabs implementation.
    ///
    /// Note: this test only covers the "no changes" (total == 0) case, since the current
    /// TestScenarioBuilder has no hook to seed a non-empty diff set for a real session.
    /// The "label shown when total &gt; 0" branch is not covered end-to-end for that reason.
    /// </summary>
    [Fact]
    public async Task ContentPanel_ChangesTabHidesReviewedLabelWhenNoChanges()
    {
        await WithFailureCapture(async () =>
        {
            ConfigureScenario(b =>
                b.WithSimpleTextResponse(
                    "_placeholder_",
                    "msg-content-panel-4",
                    "Reviewed label test response"));

            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();

            var dialog = await dashboard.ClickNewSessionAsync();
            await dialog.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));

            var detail = await dialog.SubmitAsync();
            await detail.WaitForLoadedAsync();

            // A freshly-created session against an empty temp directory has no diffs,
            // so the reviewed-count label should not render on the Changes tab.
            await Assertions.Expect(detail.GetChangesTabReviewedLabel()).ToHaveCountAsync(0);
        });
    }

    /// <summary>
    /// Verify that ArrowRight moves roving tab focus from Files to Changes.
    /// </summary>
    [Fact]
    public async Task ContentPanel_ArrowRightMovesTabFocus()
    {
        await WithFailureCapture(async () =>
        {
            ConfigureScenario(b =>
                b.WithSimpleTextResponse(
                    "_placeholder_",
                    "msg-content-panel-5",
                    "Keyboard nav test response"));

            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();

            var dialog = await dashboard.ClickNewSessionAsync();
            await dialog.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));

            var detail = await dialog.SubmitAsync();
            await detail.WaitForLoadedAsync();

            await detail.ClickTabAsync("files");
            await Assertions.Expect(detail.GetFilesTab()).ToBeFocusedAsync();

            // ArrowRight moves focus from Files to Changes (roving tabindex).
            await detail.PressKeyOnTabAsync("files", "ArrowRight");
            await Assertions.Expect(detail.GetChangesTab()).ToBeFocusedAsync();

            // ArrowRight again wraps back to Files.
            await detail.PressKeyOnTabAsync("changes", "ArrowRight");
            await Assertions.Expect(detail.GetFilesTab()).ToBeFocusedAsync();
        });
    }

    /// <summary>
    /// Verify that the panel collapse button works.
    /// </summary>
    [Fact]
    public async Task ContentPanel_CollapseButtonCollapsesPanel()
    {
        await WithFailureCapture(async () =>
        {
            ConfigureScenario(b =>
                b.WithSimpleTextResponse(
                    "_placeholder_",
                    "msg-content-panel-6",
                    "Panel collapse test response"));

            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();

            var dialog = await dashboard.ClickNewSessionAsync();
            await dialog.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));

            var detail = await dialog.SubmitAsync();
            await detail.WaitForLoadedAsync();

            // Panel should start expanded (tabs visible).
            await Assertions.Expect(detail.GetFilesTab()).ToBeVisibleAsync();

            // Click collapse button.
            await detail.ClickPanelCollapseAsync();

            // Panel should now be collapsed (collapsed rail visible).
            await Assertions.Expect(detail.GetCollapsedRightRail()).ToBeVisibleAsync();

            // Tabs should be hidden.
            await Assertions.Expect(detail.GetFilesTab()).ToBeHiddenAsync();
        });
    }

    /// <summary>
    /// Verify that the panel expand button works after collapsing.
    /// </summary>
    [Fact]
    public async Task ContentPanel_ExpandButtonExpandsPanel()
    {
        await WithFailureCapture(async () =>
        {
            ConfigureScenario(b =>
                b.WithSimpleTextResponse(
                    "_placeholder_",
                    "msg-content-panel-7",
                    "Panel expand test response"));

            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();

            var dialog = await dashboard.ClickNewSessionAsync();
            await dialog.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));

            var detail = await dialog.SubmitAsync();
            await detail.WaitForLoadedAsync();

            // Collapse the panel first.
            await detail.ClickPanelCollapseAsync();
            await Assertions.Expect(detail.GetCollapsedRightRail()).ToBeVisibleAsync();

            // Click expand button.
            await detail.ClickPanelExpandAsync();

            // Panel should now be expanded (tabs visible again).
            await Assertions.Expect(detail.GetFilesTab()).ToBeVisibleAsync();
            await Assertions.Expect(detail.GetChangesTab()).ToBeVisibleAsync();

            // Collapsed rail should be hidden.
            await Assertions.Expect(detail.GetCollapsedRightRail()).ToBeHiddenAsync();
        });
    }

    /// <summary>
    /// Verify that the panel does not auto-expand when a session is selected.
    /// This test creates a session, collapses the panel, then navigates to the dashboard
    /// and back to verify the panel stays collapsed.
    /// </summary>
    [Fact]
    public async Task ContentPanel_DoesNotAutoExpandOnSessionSelection()
    {
        await WithFailureCapture(async () =>
        {
            ConfigureScenario(b =>
                b.WithSimpleTextResponse(
                    "_placeholder_",
                    "msg-content-panel-8",
                    "No auto-expand test response"));

            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();

            var dialog = await dashboard.ClickNewSessionAsync();
            await dialog.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));

            var detail = await dialog.SubmitAsync();
            await detail.WaitForLoadedAsync();

            // Collapse the panel.
            await detail.ClickPanelCollapseAsync();
            await Assertions.Expect(detail.GetCollapsedRightRail()).ToBeVisibleAsync();

            // Navigate away and back (simulating session selection).
            await Page.GotoAsync("/");
            await dashboard.WaitForLoadedAsync();

            // Navigate back to the session.
            await Page.GoBackAsync();
            await detail.WaitForLoadedAsync();

            // Panel should still be collapsed.
            await Assertions.Expect(detail.GetCollapsedRightRail()).ToBeVisibleAsync();
            await Assertions.Expect(detail.GetFilesTab()).ToBeHiddenAsync();
        });
    }
}

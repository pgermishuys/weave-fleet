using Microsoft.Playwright;
using WeaveFleet.E2E.Infrastructure;
using WeaveFleet.E2E.Pages;

namespace WeaveFleet.E2E.Tests;

/// <summary>
/// E2E tests for the Settings page workflow.
/// </summary>
[Trait("Category", "E2E")]
[Trait("Lane", "Workflow")]
public sealed class SettingsPageTests : E2ETestBase,
    IClassFixture<FleetWebApplicationFactory>,
    IClassFixture<PlaywrightFixture>
{
    public SettingsPageTests(FleetWebApplicationFactory factory, PlaywrightFixture playwright)
        : base(factory, playwright) { }

    [Fact]
    public async Task SettingsPageSavesAndPersistsChangesAfterReload()
    {
        await WithFailureCapture(async () =>
        {
            var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
            var workspaceLabel = $"Workspace {suffix}";

            var settingsPage = new SettingsPage(Page);
            await settingsPage.GotoAsync();

            await settingsPage.SetWorkspaceLabelAsync(workspaceLabel);
            (await settingsPage.GetWorkspaceLabelAsync()).ShouldBe(workspaceLabel);

            await Page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await settingsPage.WaitForLoadedAsync();

            (await settingsPage.GetWorkspaceLabelAsync()).ShouldBe(workspaceLabel);
        });
    }

}

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
            var credentialLabel = $"Persisted API Key {suffix}";
            var secretTail = suffix[^4..];
            var secretValue = $"dummy-secret-{secretTail}";

            await StubSkillsEndpointAsync();

            var settingsPage = new SettingsPage(Page);
            await settingsPage.GotoAsync();

            await settingsPage.SetWorkspaceLabelAsync(workspaceLabel);
            (await settingsPage.GetWorkspaceLabelAsync()).ShouldBe(workspaceLabel);

            await settingsPage.AddApiKeyAsync(credentialLabel, "anthropic", "api-key", secretValue);

            var savedCredentialText = await settingsPage.GetCredentialCardTextAsync(credentialLabel);
            savedCredentialText.ShouldNotBeNull();
            savedCredentialText.ShouldContain(credentialLabel);
            savedCredentialText.ShouldContain(secretTail);

            await Page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await settingsPage.WaitForLoadedAsync();
            await settingsPage.WaitForCredentialAsync(credentialLabel);

            (await settingsPage.GetWorkspaceLabelAsync()).ShouldBe(workspaceLabel);

            var reloadedCredentialText = await settingsPage.GetCredentialCardTextAsync(credentialLabel);
            reloadedCredentialText.ShouldNotBeNull();
            reloadedCredentialText.ShouldContain(credentialLabel);
            reloadedCredentialText.ShouldContain(secretTail);
        });
    }

    private Task StubSkillsEndpointAsync()
        => Page.RouteAsync("**/api/skills", async route =>
        {
            await route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 200,
                ContentType = "application/json",
                Body = "{\"skills\":[]}"
            });
        });
}

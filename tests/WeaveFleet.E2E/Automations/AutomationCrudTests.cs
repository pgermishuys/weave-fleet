using Microsoft.Playwright;
using WeaveFleet.E2E.Infrastructure;

namespace WeaveFleet.E2E.Automations;

/// <summary>
/// E2E tests for the Automations CRUD workflow.
/// Validates the full lifecycle: create, enable/disable, and delete automations through the UI.
/// </summary>
[Trait("Category", "E2E")]
[Trait("Lane", "Workflow")]
public sealed class AutomationCrudTests : E2ETestBase,
    IClassFixture<FleetWebApplicationFactory>,
    IClassFixture<PlaywrightFixture>
{
    public AutomationCrudTests(FleetWebApplicationFactory factory, PlaywrightFixture playwright)
        : base(factory, playwright) { }

    [Fact]
    public async Task AutomationCrud_HappyPath_CreatesEnablesAndDeletesAutomation()
    {
        await WithFailureCapture(async () =>
        {
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var automationName = $"E2E Test Automation {suffix}";
            var automationPrompt = $"Test prompt for automation {suffix}";
            var cronExpression = "0 * * * *";

            // Navigate to automations page
            await Page.GotoAsync("/automations");
            await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            // Wait for page to load - check for the heading
            var heading = Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { NameString = "Automations", Exact = true });
            await Assertions.Expect(heading).ToBeVisibleAsync();

            // Click "New Automation" button
            var newAutomationButton = Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { NameString = "New Automation" });
            await newAutomationButton.ClickAsync();

            // Wait for dialog to open
            var dialogTitle = Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { NameString = "Create Automation", Exact = true });
            await Assertions.Expect(dialogTitle).ToBeVisibleAsync();

            // Fill in the form
            var nameInput = Page.Locator("#automation-name");
            await nameInput.FillAsync(automationName);

            var promptInput = Page.Locator("#automation-prompt");
            await promptInput.FillAsync(automationPrompt);

            // Ensure "Schedule" trigger type is selected (should be default)
            var scheduleButton = Page.GetByRole(AriaRole.Radio, new PageGetByRoleOptions { NameString = "Schedule" });
            await scheduleButton.ClickAsync();

            // Fill in cron expression
            var cronInput = Page.Locator("#automation-trigger-config");
            await cronInput.FillAsync(cronExpression);

            // Submit the form
            var createButton = Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { NameString = "Create Automation", Exact = true });
            await createButton.ClickAsync();

            // Wait for dialog to close
            await Assertions.Expect(dialogTitle).ToBeHiddenAsync();

            // Verify the automation appears in the list
            var automationCard = Page.Locator("article").Filter(new LocatorFilterOptions { HasText = automationName });
            await Assertions.Expect(automationCard).ToBeVisibleAsync();

            // Verify it shows as disabled initially
            var disabledBadge = automationCard.Locator("span").Filter(new LocatorFilterOptions { HasText = "Disabled" });
            await Assertions.Expect(disabledBadge).ToBeVisibleAsync();

            // Verify the prompt is displayed
            var promptText = automationCard.Locator("p").Filter(new LocatorFilterOptions { HasText = automationPrompt });
            await Assertions.Expect(promptText).ToBeVisibleAsync();

            // Verify the trigger config is displayed
            var triggerText = automationCard.Locator("code").Filter(new LocatorFilterOptions { HasText = cronExpression });
            await Assertions.Expect(triggerText).ToBeVisibleAsync();

            // Click the enable button (Play icon)
            var playButton = automationCard.GetByTitle("Run automation");
            await playButton.ClickAsync();

            // Wait a moment for the state to update
            await Page.WaitForTimeoutAsync(500);

            // Verify it now shows as enabled
            var enabledBadge = automationCard.Locator("span").Filter(new LocatorFilterOptions { HasText = "Enabled" });
            await Assertions.Expect(enabledBadge).ToBeVisibleAsync();

            // Verify the pause button is now visible
            var pauseButton = automationCard.GetByTitle("Pause automation");
            await Assertions.Expect(pauseButton).ToBeVisibleAsync();

            // Click the pause button to disable
            await pauseButton.ClickAsync();

            // Wait a moment for the state to update
            await Page.WaitForTimeoutAsync(500);

            // Verify it shows as disabled again
            await Assertions.Expect(disabledBadge).ToBeVisibleAsync();

            // Click the delete button
            var deleteButton = automationCard.GetByTitle("Delete automation");
            await deleteButton.ClickAsync();

            // Wait for confirmation dialog
            var deleteDialogTitle = Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { NameString = "Delete Automation", Exact = true });
            await Assertions.Expect(deleteDialogTitle).ToBeVisibleAsync();

            // Confirm deletion
            var confirmDeleteButton = Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { NameString = "Delete", Exact = true });
            await confirmDeleteButton.ClickAsync();

            // Wait for dialog to close
            await Assertions.Expect(deleteDialogTitle).ToBeHiddenAsync();

            // Verify the automation is gone from the list
            await Assertions.Expect(automationCard).ToBeHiddenAsync();
        });
    }
}

using WeaveFleet.E2E.Infrastructure;
using WeaveFleet.E2E.Pages;

namespace WeaveFleet.E2E.Tests;

/// <summary>
/// E2E tests for the question tool card rendering.
/// Verifies that question tool calls are displayed as readable text with numbered options
/// instead of falling through to the generic collapsible tool call.
/// </summary>
[Trait("Category", "E2E")]
public sealed class QuestionToolCardTests : E2ETestBase,
    IClassFixture<FleetWebApplicationFactory>,
    IClassFixture<PlaywrightFixture>
{
    public QuestionToolCardTests(FleetWebApplicationFactory factory, PlaywrightFixture playwright)
        : base(factory, playwright) { }

    /// <summary>
    /// When the assistant emits a question tool call, the UI should render
    /// the question text and numbered options as readable content.
    /// </summary>
    [Fact]
    public async Task QuestionToolCall_RendersQuestionAndOptions()
    {
        await WithFailureCapture(async () =>
        {
            ConfigureScenario(b =>
                b.WithQuestionToolResponse(
                    "_placeholder_",
                    "msg-question-1",
                    "How would you like to proceed with the fix?",
                    "Fix approach",
                    [
                        ("Update workflows", "Upgrade action versions and improve CI"),
                        ("Remove CodeQL", "Remove the CodeQL workflow entirely"),
                        ("Keep as-is", "Leave everything unchanged")
                    ]));

            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();

            var dialog = await dashboard.ClickNewSessionAsync();
            await dialog.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));

            var detail = await dialog.SubmitAsync();
            await detail.WaitForLoadedAsync();

            // Send a prompt to trigger the question tool response
            await detail.SendPromptAsync("Investigate the CI failures");

            // Wait for the session to go idle (response complete)
            await detail.WaitForIdleAsync();

            // The question text should be visible
            var questionPrompt = Page.GetByTestId("question-prompt");
            await Microsoft.Playwright.Assertions.Expect(questionPrompt).ToBeVisibleAsync(
                new Microsoft.Playwright.LocatorAssertionsToBeVisibleOptions { Timeout = 5_000 });
            await Microsoft.Playwright.Assertions.Expect(questionPrompt)
                .ToContainTextAsync("How would you like to proceed with the fix?");

            // All three options should be rendered
            var options = Page.GetByTestId("question-option");
            await Microsoft.Playwright.Assertions.Expect(options).ToHaveCountAsync(3,
                new Microsoft.Playwright.LocatorAssertionsToHaveCountOptions { Timeout = 5_000 });

            // Verify option labels are present
            await Microsoft.Playwright.Assertions.Expect(options.Nth(0))
                .ToContainTextAsync("Update workflows");
            await Microsoft.Playwright.Assertions.Expect(options.Nth(1))
                .ToContainTextAsync("Remove CodeQL");
            await Microsoft.Playwright.Assertions.Expect(options.Nth(2))
                .ToContainTextAsync("Keep as-is");

            // Verify option descriptions are present
            await Microsoft.Playwright.Assertions.Expect(options.Nth(0))
                .ToContainTextAsync("Upgrade action versions and improve CI");
            await Microsoft.Playwright.Assertions.Expect(options.Nth(1))
                .ToContainTextAsync("Remove the CodeQL workflow entirely");

            // The hint text should be visible (since the tool is in running state)
            await Microsoft.Playwright.Assertions.Expect(questionPrompt)
                .ToContainTextAsync("Reply with your choice to continue");
        });
    }

    /// <summary>
    /// When the assistant emits a question tool call with a single option,
    /// the UI should still render correctly.
    /// </summary>
    [Fact]
    public async Task QuestionToolCall_SingleOption_RendersCorrectly()
    {
        await WithFailureCapture(async () =>
        {
            ConfigureScenario(b =>
                b.WithQuestionToolResponse(
                    "_placeholder_",
                    "msg-question-2",
                    "Do you want to continue?",
                    "Confirm",
                    [("Yes, continue", "Proceed with the changes")]));

            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();

            var dialog = await dashboard.ClickNewSessionAsync();
            await dialog.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));

            var detail = await dialog.SubmitAsync();
            await detail.WaitForLoadedAsync();

            await detail.SendPromptAsync("Make the change");
            await detail.WaitForIdleAsync();

            // Question should be visible
            var questionPrompt = Page.GetByTestId("question-prompt");
            await Microsoft.Playwright.Assertions.Expect(questionPrompt).ToBeVisibleAsync(
                new Microsoft.Playwright.LocatorAssertionsToBeVisibleOptions { Timeout = 5_000 });
            await Microsoft.Playwright.Assertions.Expect(questionPrompt)
                .ToContainTextAsync("Do you want to continue?");

            // Single option should be rendered
            var options = Page.GetByTestId("question-option");
            await Microsoft.Playwright.Assertions.Expect(options).ToHaveCountAsync(1,
                new Microsoft.Playwright.LocatorAssertionsToHaveCountOptions { Timeout = 5_000 });
            await Microsoft.Playwright.Assertions.Expect(options.First)
                .ToContainTextAsync("Yes, continue");
        });
    }
}

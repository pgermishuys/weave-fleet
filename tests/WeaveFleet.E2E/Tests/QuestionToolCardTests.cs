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

    /// <summary>
    /// Regression: when a question tool is pending, the user's reply must be sent
    /// immediately instead of being queued. Previously the prompt input would queue
    /// the reply (because the session appeared "busy"), creating a deadlock where
    /// the session waited for the answer while the answer waited for idle.
    /// </summary>
    [Fact]
    public async Task QuestionReply_BypassesQueueAndIsProcessed()
    {
        await WithFailureCapture(async () =>
        {
            // First prompt → question tool response; second prompt → normal text reply
            ConfigureScenario(b =>
                b.WithQuestionToolResponse(
                    "_placeholder_",
                    "msg-question-3",
                    "Which approach do you prefer?",
                    "Approach",
                    [
                        ("Option A", "First approach"),
                        ("Option B", "Second approach")
                    ])
                .WithSimpleTextResponse(
                    "_placeholder_",
                    "msg-reply-3",
                    "Great, proceeding with Option A."));

            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();

            var dialog = await dashboard.ClickNewSessionAsync();
            await dialog.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));

            var detail = await dialog.SubmitAsync();
            await detail.WaitForLoadedAsync();

            // Send prompt that triggers the question
            await detail.SendPromptAsync("Help me decide");
            await detail.WaitForIdleAsync();

            // Verify the question appeared
            var questionPrompt = Page.GetByTestId("question-prompt");
            await Microsoft.Playwright.Assertions.Expect(questionPrompt).ToBeVisibleAsync(
                new Microsoft.Playwright.LocatorAssertionsToBeVisibleOptions { Timeout = 5_000 });

            // Verify the prompt placeholder indicates a pending question
            var promptInput = Page.GetByTestId("prompt-input");
            await Microsoft.Playwright.Assertions.Expect(promptInput)
                .ToHaveAttributeAsync("placeholder", "Reply to the question above…",
                    new Microsoft.Playwright.LocatorAssertionsToHaveAttributeOptions { Timeout = 5_000 });

            // Reply to the question — this must NOT be queued
            await detail.SendPromptAsync("Option A");
            await detail.WaitForIdleAsync();

            // The assistant's follow-up response should appear, proving the reply was processed
            await detail.WaitForMessageTextAsync("Great, proceeding with Option A.");

            // Placeholder should revert to the default idle text
            await Microsoft.Playwright.Assertions.Expect(promptInput)
                .ToHaveAttributeAsync("placeholder", "Send a message to this session…",
                    new Microsoft.Playwright.LocatorAssertionsToHaveAttributeOptions { Timeout = 5_000 });
        });
    }
}

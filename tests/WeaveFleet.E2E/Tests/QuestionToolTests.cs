using WeaveFleet.E2E.Infrastructure;
using WeaveFleet.E2E.Pages;

namespace WeaveFleet.E2E.Tests;

/// <summary>
/// E2E tests for the question tool lifecycle: presenting a question form,
/// selecting an answer, submitting, and verifying the answered state renders
/// both the question text and the chosen answer.
/// </summary>
[Trait("Category", "E2E")]
[Trait("Lane", "Workflow")]
public sealed class QuestionToolTests : E2ETestBase,
    IClassFixture<FleetWebApplicationFactory>,
    IClassFixture<PlaywrightFixture>
{
    public QuestionToolTests(FleetWebApplicationFactory factory, PlaywrightFixture playwright)
        : base(factory, playwright) { }

    /// <summary>
    /// Verifies the full question tool lifecycle:
    /// 1. A prompt triggers an assistant message with a question tool part.
    /// 2. The active question card renders with options.
    /// 3. The user selects an option and submits.
    /// 4. The answered card displays the question text and chosen answer.
    /// </summary>
    [Fact]
    public async Task QuestionTool_SelectAndSubmit_ShowsAnsweredStateWithQuestionAndAnswer()
    {
        await WithFailureCapture(async () =>
        {
            var questionInput = new
            {
                questions = new[]
                {
                    new
                    {
                        header = "Deployment target",
                        question = "Which environment should we deploy to?",
                        options = new[]
                        {
                            new { label = "Production", description = "Live environment" },
                            new { label = "Staging", description = "Pre-production testing" },
                            new { label = "Development", description = "Local dev cluster" }
                        },
                        multiple = false,
                        custom = false
                    }
                }
            };

            ConfigureScenario(b => b
                .WithQuestionToolResponse(
                    "_placeholder_",
                    "msg-question-1",
                    "call-q1",
                    questionInput));

            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();

            var dialog = await dashboard.ClickNewSessionAsync();
            await dialog.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));

            var detail = await dialog.SubmitAsync();
            await detail.WaitForLoadedAsync();

            // Send a prompt to trigger the question
            await detail.SendPromptAsync("Deploy the app");
            await detail.WaitForBusyAsync();

            // Wait for the active question card to appear
            var activeCard = Page.GetByTestId("question-card-active");
            await Microsoft.Playwright.Assertions.Expect(activeCard).ToBeVisibleAsync(
                new Microsoft.Playwright.LocatorAssertionsToBeVisibleOptions { Timeout = 10_000 });

            // Verify the question text is shown
            await Microsoft.Playwright.Assertions.Expect(activeCard).ToContainTextAsync(
                "Which environment should we deploy to?");

            // Select "Staging" option
            var stagingPill = Page.GetByTestId("question-pill-Staging");
            await stagingPill.ClickAsync();

            // Submit the answer
            var submitButton = Page.GetByTestId("question-submit-button");
            await submitButton.ClickAsync();

            // Wait for the answered card to appear
            var answeredCard = Page.GetByTestId("question-card-answered");
            await Microsoft.Playwright.Assertions.Expect(answeredCard).ToBeVisibleAsync(
                new Microsoft.Playwright.LocatorAssertionsToBeVisibleOptions { Timeout = 10_000 });

            // Verify the answered card shows the question text
            await Microsoft.Playwright.Assertions.Expect(answeredCard).ToContainTextAsync(
                "Which environment should we deploy to?");

            // Verify the answered card shows the selected answer
            await Microsoft.Playwright.Assertions.Expect(answeredCard).ToContainTextAsync("Staging");
        });
    }
}

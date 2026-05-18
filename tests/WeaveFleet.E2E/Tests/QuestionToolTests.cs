using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.E2E.Infrastructure;
using WeaveFleet.E2E.Pages;
using WeaveFleet.Infrastructure.Data.Repositories;
using WeaveFleet.TestHarness;
using WeaveFleet.Testing.Fakes;

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
    private readonly FleetWebApplicationFactory _factory;

    public QuestionToolTests(FleetWebApplicationFactory factory, PlaywrightFixture playwright)
        : base(factory, playwright)
    {
        _factory = factory;
    }

    /// <summary>
    /// Verifies the full question tool lifecycle using DB-seeded messages:
    /// 1. A persisted assistant message with a running question tool part is loaded.
    /// 2. The active question card renders with options.
    /// 3. The user selects an option and submits.
    /// 4. The answered card displays the question text and chosen answer.
    /// </summary>
    [Fact]
    public async Task QuestionTool_SelectAndSubmit_ShowsAnsweredStateWithQuestionAndAnswer()
    {
        await WithFailureCapture(async () =>
        {
            ConfigureScenario(_ => { });

            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();

            var dialog = await dashboard.ClickNewSessionAsync();
            await dialog.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));

            var detail = await dialog.SubmitAsync();
            await detail.WaitForLoadedAsync();

            // Extract session and instance IDs from URL
            var sessionUri = new Uri(Page.Url);
            var sessionId = sessionUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();
            var instanceId = System.Web.HttpUtility.ParseQueryString(sessionUri.Query)["instanceId"]!;

            // Get the live TestHarnessSession via InstanceTracker
            var tracker = _factory.KestrelServices.GetRequiredService<InstanceTracker>();
            var harness = tracker.Get(instanceId).ShouldBeOfType<TestHarnessSession>();

            // Build the question input
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

            var messageId = $"msg-question-{Guid.NewGuid():N}";
            var toolCallId = "call-q1";

            // Seed an assistant message with a running question tool part
            var connFactory = _factory.KestrelServices.GetRequiredService<IDbConnectionFactory>();
            var userContext = new TestUserContext("local-user");
            var messageRepo = new MessageRepository(connFactory, userContext);

            await messageRepo.UpsertAsync(
                MessagePersistenceService.ToPersistedMessage(
                    sessionId,
                    new HarnessMessage
                    {
                        Id = messageId,
                        Role = "assistant",
                        Parts =
                        [
                            new ToolUsePart(
                                ToolCallId: toolCallId,
                                ToolName: "question",
                                Arguments: JsonSerializer.SerializeToElement(questionInput),
                                State: ToolUseState.Running),
                        ],
                        Timestamp = DateTimeOffset.UtcNow,
                    }));

            // Set question context on the harness so AnswerQuestionAsync can emit completion
            harness.SetQuestionContext(messageId, JsonSerializer.SerializeToElement(questionInput));

            // Reload the page to pick up persisted messages
            await detail.GotoAsync(sessionId, instanceId);

            // Wait for the active question card to appear
            var activeCard = Page.GetByTestId("question-card-active");
            await Assertions.Expect(activeCard).ToBeVisibleAsync(
                new LocatorAssertionsToBeVisibleOptions { Timeout = 10_000 });

            // Verify the question text is shown
            await Assertions.Expect(activeCard).ToContainTextAsync(
                "Which environment should we deploy to?");

            // Select "Staging" option
            var stagingPill = Page.GetByTestId("question-pill-Staging");
            await stagingPill.ClickAsync();

            // Submit the answer
            var submitButton = Page.GetByTestId("question-submit-button");
            await submitButton.ClickAsync();

            // Wait for the answered card to appear
            var answeredCard = Page.GetByTestId("question-card-answered");
            await Assertions.Expect(answeredCard).ToBeVisibleAsync(
                new LocatorAssertionsToBeVisibleOptions { Timeout = 10_000 });

            // Verify the answered card shows the question text
            await Assertions.Expect(answeredCard).ToContainTextAsync(
                "Which environment should we deploy to?");

            // Verify the answered card shows the selected answer
            await Assertions.Expect(answeredCard).ToContainTextAsync("Staging");
        });
    }
}

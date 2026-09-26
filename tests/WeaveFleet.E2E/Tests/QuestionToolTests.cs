using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.E2E.Infrastructure;
using WeaveFleet.E2E.Pages;
using WeaveFleet.TestHarness;

namespace WeaveFleet.E2E.Tests;

/// <summary>
/// E2E tests for the question tool lifecycle: presenting a question form,
/// selecting an answer, submitting, and verifying the answered state renders
/// both the question text and the chosen answer.
/// </summary>
[Trait("Category", "E2E")]
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
    /// Verifies the full question tool lifecycle:
    /// 1. The harness emits an assistant message with a running question tool part.
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
                            new { label = "Production", description = "" },
                            new { label = "Staging", description = "" },
                            new { label = "Development", description = "" }
                        },
                        multiple = false,
                        custom = false
                    }
                }
            };

            var messageId = $"msg-question-{Guid.NewGuid():N}";
            var toolCallId = "call-q1";

            // The agent asks inside a turn, as a real harness does: a question call is only live while the session is
            // in one. Outside a turn Fleet reads a running call as cut off.
            await harness.PushEventAsync(new HarnessEvent
            {
                Type = "session.status",
                SessionId = harness.InstanceId,
                FleetSessionId = sessionId,
                Timestamp = DateTimeOffset.UtcNow,
                Payload = JsonSerializer.SerializeToElement(new { sessionId = harness.InstanceId, status = new { type = "busy" } }),
            });

            // The agent asks the question: an assistant message with a running question tool part
            await harness.PushEventAsync(new HarnessEvent
            {
                Type = "message.updated",
                SessionId = harness.InstanceId,
                FleetSessionId = sessionId,
                Timestamp = DateTimeOffset.UtcNow,
                Payload = JsonSerializer.SerializeToElement(new
                {
                    info = new
                    {
                        id = messageId,
                        sessionID = harness.InstanceId,
                        role = "assistant",
                        time = new { created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
                    },
                }),
            });
            await harness.PushEventAsync(new HarnessEvent
            {
                Type = "message.part.updated",
                SessionId = harness.InstanceId,
                FleetSessionId = sessionId,
                Timestamp = DateTimeOffset.UtcNow,
                Payload = JsonSerializer.SerializeToElement(new
                {
                    sessionID = harness.InstanceId,
                    part = new
                    {
                        type = "tool",
                        id = toolCallId,
                        callID = toolCallId,
                        tool = "question",
                        sessionID = harness.InstanceId,
                        messageID = messageId,
                        state = new { status = "running", input = questionInput },
                    },
                }),
            });

            // Set question context on the harness so AnswerQuestionAsync can emit completion
            harness.SetQuestionContext(messageId, JsonSerializer.SerializeToElement(questionInput));

            // Reload: the question must survive a page load, not just arrive live
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

            // Submit the answer (fires API call)
            var submitButton = Page.GetByTestId("question-submit-button");
            await submitButton.ClickAsync();

            // The chosen answer must reach the harness through the API.
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (harness.LastAnswers is null && DateTimeOffset.UtcNow < deadline)
                await Task.Delay(50);
            harness.LastAnswers.ShouldNotBeNull("The submitted answer never reached the harness.");
            harness.LastAnswers.ShouldHaveSingleItem().ShouldBe(["Staging"]);

            // The completion event the test harness emits carries no Fleet session ID, so
            // push one that does to drive the answered state in the browser.
            await harness.PushEventAsync(new HarnessEvent
            {
                Type = "message.part.updated",
                SessionId = harness.InstanceId,
                FleetSessionId = sessionId,
                Timestamp = DateTimeOffset.UtcNow,
                Payload = JsonSerializer.SerializeToElement(new
                {
                    sessionID = harness.InstanceId,
                    part = new
                    {
                        type = "tool",
                        id = toolCallId,
                        callID = toolCallId,
                        tool = "question",
                        sessionID = harness.InstanceId,
                        messageID = messageId,
                        state = new
                        {
                            status = "completed",
                            input = questionInput,
                            metadata = new { answers = new string[][] { ["Staging"] } }
                        }
                    }
                })
            });

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

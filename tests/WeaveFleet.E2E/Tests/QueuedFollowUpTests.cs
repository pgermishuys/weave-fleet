using System.Text.Json;
using Microsoft.Playwright;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.E2E.Infrastructure;
using WeaveFleet.E2E.Pages;
using WeaveFleet.TestHarness;

namespace WeaveFleet.E2E.Tests;

/// <summary>
/// A prompt queued during a turn is sent the moment the turn ends. Nobody should hear "Finished its turn." or see the
/// row go idle in between: the session carries on, and finishes once, after the queued turn.
/// </summary>
[Trait("Category", "E2E")]
public sealed class QueuedFollowUpTests : E2ETestBase,
    IClassFixture<FleetWebApplicationFactory>,
    IClassFixture<PlaywrightFixture>
{
    public QueuedFollowUpTests(FleetWebApplicationFactory factory, PlaywrightFixture playwright)
        : base(factory, playwright) { }

    [Fact]
    public async Task A_queued_follow_up_runs_without_a_finished_notice_or_an_idle_in_between()
    {
        await WithFailureCapture(async () =>
        {
            // The first turn stays busy until the follow-up is queued, so the queue can never land after the turn ended.
            var followUpQueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ConfigureScenario(builder => builder
                .WithPromptResponse(response => Turn(response, 1_500, followUpQueued.Task))
                .WithPromptResponse(response => Turn(response, 1_500)));

            var frames = new List<string>();
            Page.WebSocket += (_, socket) => socket.FrameReceived += (_, frame) =>
            {
                if (frame.Text is { } text && text.Contains("\"sessions\"", StringComparison.Ordinal))
                    lock (frames)
                        frames.Add(text);
            };

            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();
            var dialog = await dashboard.ClickNewSessionAsync();
            await dialog.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
            await dialog.SetTitleAsync("Queued follow-up");
            var detail = await dialog.SubmitAsync();
            await detail.WaitForLoadedAsync();
            var sessionId = new Uri(Page.Url).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();

            await detail.SendPromptAsync("First request", 30_000);

            var queued = await Page.APIRequest.PostAsync($"/api/sessions/{sessionId}/queue", new()
            {
                DataString = JsonSerializer.Serialize(new { text = "Second request" }),
                Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" },
            });
            Assert.True(queued.Ok, await queued.TextAsync());
            followUpQueued.SetResult();

            // Wait on what we're asserting: the finish notice for the session (it comes after the queued turn).
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                lock (frames)
                    if (frames.Any(f => f.Contains(sessionId, StringComparison.Ordinal) && f.Contains("\"session_notification\"", StringComparison.Ordinal) && f.Contains("\"finished\"", StringComparison.Ordinal)))
                        break;
                await Task.Delay(100);
            }
            await Page.ScreenshotAsync(new() { Path = Environment.GetEnvironmentVariable("QUEUED_SHOT") ?? Path.Combine(Path.GetTempPath(), "queued-follow-up.png") });

            string[] seen;
            lock (frames)
                seen = [.. frames];

            var statuses = seen
                .Where(f => f.Contains(sessionId, StringComparison.Ordinal))
                .Select(f => f.Contains("\"session_notification\"", StringComparison.Ordinal) && f.Contains("\"finished\"", StringComparison.Ordinal) ? "finished"
                    : f.Contains("\"activityStatus\":\"idle\"", StringComparison.Ordinal) ? "idle"
                    : f.Contains("\"activityStatus\":\"busy\"", StringComparison.Ordinal) ? "busy" : null)
                .Where(s => s is not null)
                .ToList();

            // Ignore the idle from creating the session; from the first busy on, no idle comes before the one finish.
            var fromWork = statuses.Skip(statuses.IndexOf("busy")).ToList();
            var log = string.Join(",", fromWork);
            Assert.Equal(1, fromWork.Count(s => s == "finished"));
            var finished = fromWork.IndexOf("finished");
            Assert.True(fromWork.LastIndexOf("busy") < finished, log);
            Assert.DoesNotContain("idle", fromWork.Take(finished));
        });
    }

    private static PromptResponseBuilder Turn(PromptResponseBuilder response, int millis, Task? gate = null)
        => response
            .AddEvent(Make(EventTypes.SessionStatus, new { sessionId = "_placeholder_", status = new { type = "busy" } }))
            .AddEvent(Make(EventTypes.SessionStatus, new { sessionId = "_placeholder_", status = new { type = "idle" } }), TimeSpan.FromMilliseconds(millis), gate)
            .AddEvent(Make(EventTypes.SessionIdle, new { sessionId = "_placeholder_" }), TimeSpan.FromMilliseconds(50));

    private static HarnessEvent Make(string type, object payload)
        => new()
        {
            Type = type,
            SessionId = "_placeholder_",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(payload),
        };
}

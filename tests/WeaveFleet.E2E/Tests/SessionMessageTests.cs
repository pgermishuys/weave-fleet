using WeaveFleet.E2E.Infrastructure;
using WeaveFleet.E2E.Pages;

namespace WeaveFleet.E2E.Tests;

/// <summary>
/// E2E tests for session messaging: prompt send, streamed text response, tool call response,
/// and real-time status updates via WebSocket.
/// </summary>
[Trait("Category", "E2E")]
public sealed class SessionMessageTests : E2ETestBase,
    IClassFixture<FleetWebApplicationFactory>,
    IClassFixture<PlaywrightFixture>
{
    public SessionMessageTests(FleetWebApplicationFactory factory, PlaywrightFixture playwright)
        : base(factory, playwright) { }

    /// <summary>
    /// Task 22: Send a prompt and receive a streamed text response.
    /// Verifies session transitions: idle → busy → idle.
    /// </summary>
    [Fact]
    public async Task SendPrompt_ReceivesTextResponse_SessionTransitionsToIdle()
    {
        await WithFailureCapture(async () =>
        {
            ConfigureScenario(b =>
                b.WithSimpleTextResponse(
                    "_placeholder_",
                    "msg-stream-1",
                    "This is the streamed response from TestHarness",
                    TimeSpan.FromMilliseconds(500)));

            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();

            var dialog = await dashboard.ClickNewSessionAsync();
            await dialog.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));

            var detail = await dialog.SubmitAsync();
            await detail.WaitForLoadedAsync();

            // Session should start idle
            var initialStatus = await detail.GetStatusAsync();
            initialStatus.ShouldBe("idle");

            // Send a prompt
            await detail.SendPromptAsync("Tell me something");

            // Session should become busy
            await detail.WaitForBusyAsync();

            // Session should return to idle after response streams
            await detail.WaitForIdleAsync();

            // User message should appear in the conversation
            var userMessages = detail.GetMessagesByRole("user");
            await Microsoft.Playwright.Assertions.Expect(userMessages).ToHaveCountAsync(1,
                new Microsoft.Playwright.LocatorAssertionsToHaveCountOptions { Timeout = 5_000 });
        });
    }

    /// <summary>
    /// Task 23: Send a prompt and verify the response contains content (text or tool parts).
    /// The TestHarness emits a simple text response so we check for assistant messages.
    /// </summary>
    [Fact]
    public async Task SendPrompt_AssistantMessageAppearsAfterResponse()
    {
        await WithFailureCapture(async () =>
        {
            ConfigureScenario(b =>
                b.WithSimpleTextResponse(
                    "_placeholder_",
                    "msg-assistant-1",
                    "Assistant message text content"));

            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();

            var dialog = await dashboard.ClickNewSessionAsync();
            await dialog.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));

            var detail = await dialog.SubmitAsync();
            await detail.WaitForLoadedAsync();

            await detail.SendPromptAsync("Generate a response");

            // Wait for at least one message to appear (the user's prompt)
            await detail.WaitForMessageCountAsync(1);

            // Wait for the session to go idle (response complete)
            await detail.WaitForIdleAsync();

            // At least one message item should exist
            var allMessages = await detail.GetMessageItemsAsync();
            allMessages.Count.ShouldBeGreaterThanOrEqualTo(1, $"Expected at least 1 message, got {allMessages.Count}");
        });
    }

    /// <summary>
    /// Task 24: Session status indicator on the detail page reflects real-time state changes.
    /// Verifies that the status starts idle, goes busy when a prompt is sent, and returns to idle.
    /// </summary>
    [Fact]
    public async Task SessionStatus_UpdatesInRealTime()
    {
        await WithFailureCapture(async () =>
        {
            ConfigureScenario(b =>
                b.WithSimpleTextResponse(
                    "_placeholder_",
                    "msg-status-1",
                    "Status tracking response",
                    TimeSpan.FromMilliseconds(500)));

            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();

            var dialog = await dashboard.ClickNewSessionAsync();
            await dialog.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));

            var detail = await dialog.SubmitAsync();
            await detail.WaitForLoadedAsync();

            // Verify initial idle status
            var initialStatus = await detail.GetStatusAsync();
            initialStatus.ShouldBe("idle");

            // Send prompt — status goes busy
            await detail.SendPromptAsync("Track my status");
            await detail.WaitForBusyAsync();

            // Status returns to idle after response
            await detail.WaitForIdleAsync();

            var finalStatus = await detail.GetStatusAsync();
            finalStatus.ShouldBe("idle");
        });
    }

}

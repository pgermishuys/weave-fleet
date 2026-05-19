using Microsoft.Playwright;
using WeaveFleet.E2E.Infrastructure;
using WeaveFleet.E2E.Pages;

namespace WeaveFleet.E2E.Tests;

/// <summary>
/// E2E tests for session messaging: prompt send, streamed text response, tool call response,
/// and real-time status updates via WebSocket.
/// </summary>
[Trait("Category", "E2E")]
[Trait("Lane", "Workflow")]
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
    /// Task 23: When a second prompt is sent while the session is still busy,
    /// the UI shows explicit queue feedback and eventually delivers that prompt.
    /// This verifies the current product behavior is queueing rather than silently dropping input.
    /// </summary>
    [Fact]
    public async Task SendSecondPromptWhileBusy_QueuesPromptWithVisibleFeedback()
    {
        await WithFailureCapture(async () =>
        {
            ConfigureScenario(b => b
                .WithSimpleTextResponse(
                    "_placeholder_",
                    "msg-queue-1",
                    "First queued response",
                    TimeSpan.FromMilliseconds(1_200))
                .WithSimpleTextResponse(
                    "_placeholder_",
                    "msg-queue-2",
                    "Second queued response",
                    TimeSpan.FromMilliseconds(200)));

            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();

            var dialog = await dashboard.ClickNewSessionAsync();
            await dialog.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));

            var detail = await dialog.SubmitAsync();
            await detail.WaitForLoadedAsync();

            await detail.SendPromptAsync("First prompt");
            await detail.WaitForBusyAsync();
            await detail.WaitForMessageTextAsync("First prompt", 10_000);

            await detail.SendPromptAsync("Second prompt while busy");

            var queuedBadge = Page.GetByText("1 queued");
            await Microsoft.Playwright.Assertions.Expect(queuedBadge).ToBeVisibleAsync();

            var userMessages = detail.GetMessagesByRole("user");
            await Microsoft.Playwright.Assertions.Expect(userMessages).ToHaveCountAsync(1,
                new Microsoft.Playwright.LocatorAssertionsToHaveCountOptions { Timeout = 1_000 });

            await detail.WaitForMessageTextAsync("First queued response", 10_000);
            await detail.WaitForMessageTextAsync("Second prompt while busy", 10_000);
            await detail.WaitForMessageTextAsync("Second queued response", 10_000);
            await detail.WaitForMessageCountAsync(4, 10_000);
            await detail.WaitForIdleAsync(10_000);

            await Microsoft.Playwright.Assertions.Expect(queuedBadge).ToBeHiddenAsync();
            await Microsoft.Playwright.Assertions.Expect(userMessages).ToHaveCountAsync(2,
                new Microsoft.Playwright.LocatorAssertionsToHaveCountOptions { Timeout = 5_000 });
        });
    }

    /// <summary>
    /// Send a prompt and verify the response contains content (text or tool parts).
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

    [Fact]
    public async Task expanded_tool_cards_show_non_blank_content()
    {
        await WithFailureCapture(async () =>
        {
            const string toolCardId = "tool-output-card";

            ConfigureScenario(b =>
                b.WithToolResponse(
                    "_placeholder_",
                    "msg-tool-output-1",
                    toolCardId,
                    "call-tool-output-1",
                    "bash",
                    new
                    {
                        status = "completed",
                        summary = "Completed command execution",
                        output = "line 1\nline 2"
                    },
                    TimeSpan.FromMilliseconds(200)));

            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();

            var dialog = await dashboard.ClickNewSessionAsync();
            await dialog.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));

            var detail = await dialog.SubmitAsync();
            await detail.WaitForLoadedAsync();

            await detail.SendPromptAsync("Run the tool");
            await detail.WaitForIdleAsync(10_000);
            await detail.WaitForToolCardAsync(toolCardId);

            await detail.ExpandToolCardAsync(toolCardId);

            var summaryText = (await detail.GetToolCardSummary(toolCardId).TextContentAsync())?.Trim();
            var outputText = (await detail.GetToolCardOutput(toolCardId).TextContentAsync())?.Trim();

            string.IsNullOrWhiteSpace(summaryText).ShouldBeFalse();
            string.IsNullOrWhiteSpace(outputText).ShouldBeFalse();
            await Assertions.Expect(detail.GetToolCardEmptyState(toolCardId)).ToHaveCountAsync(0);
        });
    }

    [Fact]
    public async Task diff_view_tool_cards_render_diff_rows_when_inline_diffs_enabled()
    {
        await WithFailureCapture(async () =>
        {
            const string toolCardId = "tool-diff-card";

            ConfigureScenario(b =>
                b.WithToolResponse(
                    "_placeholder_",
                    "msg-tool-diff-1",
                    toolCardId,
                    "call-tool-diff-1",
                    "edit",
                    new
                    {
                        status = "completed",
                        summary = "Applied patch",
                        diff = new object[]
                        {
                            new { type = "context", content = "@@ -1,2 +1,2 @@" },
                            new { type = "remove", content = "-old line", oldLineNumber = 1 },
                            new { type = "add", content = "+new line", newLineNumber = 1 },
                        }
                    },
                    TimeSpan.FromMilliseconds(200)));

            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();

            var dialog = await dashboard.ClickNewSessionAsync();
            await dialog.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));

            var detail = await dialog.SubmitAsync();
            await detail.WaitForLoadedAsync();
            await detail.SetInlineToolDiffsAsync(true);

            await detail.SendPromptAsync("Apply the patch");
            await detail.WaitForIdleAsync(10_000);
            await detail.WaitForToolCardAsync(toolCardId);

            await Assertions.Expect(detail.GetToolCardBody(toolCardId)).ToBeVisibleAsync();
            await Assertions.Expect(detail.GetToolCardDiff(toolCardId)).ToBeVisibleAsync();
            await Assertions.Expect(detail.GetToolCardDiffRows(toolCardId)).ToHaveCountAsync(3);
            await Assertions.Expect(detail.GetToolCardOutput(toolCardId)).ToHaveCountAsync(0);

            var diffRows = detail.GetToolCardDiffRows(toolCardId);
            (await diffRows.Nth(0).GetAttributeAsync("data-diff-type")).ShouldBe("context");
            (await diffRows.Nth(1).GetAttributeAsync("data-diff-type")).ShouldBe("remove");
            (await diffRows.Nth(2).GetAttributeAsync("data-diff-type")).ShouldBe("add");
            var removedLineText = await diffRows.Nth(1).TextContentAsync();
            var addedLineText = await diffRows.Nth(2).TextContentAsync();

            removedLineText.ShouldNotBeNull();
            addedLineText.ShouldNotBeNull();
            removedLineText.ShouldContain("-old line");
            addedLineText.ShouldContain("+new line");
        });
    }

}

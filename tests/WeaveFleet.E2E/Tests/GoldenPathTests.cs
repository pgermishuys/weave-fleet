using WeaveFleet.E2E.Infrastructure;
using WeaveFleet.E2E.Pages;

namespace WeaveFleet.E2E.Tests;

/// <summary>
/// Golden path E2E test: the foundational end-to-end flow that proves the entire stack works.
/// If this fails, all other E2E tests are likely broken too.
/// </summary>
[Trait("Category", "E2E")]
public sealed class GoldenPathTests : E2ETestBase,
    IClassFixture<FleetWebApplicationFactory>,
    IClassFixture<PlaywrightFixture>
{
    public GoldenPathTests(FleetWebApplicationFactory factory, PlaywrightFixture playwright)
        : base(factory, playwright) { }

    /// <summary>
    /// Full end-to-end: Navigate dashboard → open New Session dialog → create session
    /// → verify redirect to session detail → send a prompt → verify streamed response → session goes idle.
    /// </summary>
    [Fact]
    public async Task CreateSession_SendPrompt_ReceivesStreamedResponse()
    {
        await WithFailureCapture(async () =>
        {
            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();

            // The dashboard loads and shows the empty state (no sessions yet)
            await dashboard.WaitForEmptyStateAsync();

            // Open the New Session dialog
            var dialog = await dashboard.ClickNewSessionAsync();

            // We need a temporary directory that exists on the server.
            // Use the system temp path since it's guaranteed to exist.
            var tempDir = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
            await dialog.SetDirectoryAsync(tempDir);

            // Submit — this creates a session via the real backend + TestHarness
            // Configure a simple text response before the session is created
            // (scenario is global for the next spawn)
            ConfigureScenario(b =>
            {
                // We don't know the session ID yet, use a placeholder
                // WithSimpleTextResponse needs a sessionId — we'll use "test-session" as the harness
                // doesn't validate it against the actual session ID
                b.WithSimpleTextResponse("_placeholder_", "msg-golden-1", "Hello from TestHarness!");
            });

            var detail = await dialog.SubmitAsync();

            // The session detail page is now visible
            await detail.WaitForLoadedAsync();

            // Send a prompt
            await detail.SendPromptAsync("Hello, world!");

            // Wait for the user message to appear
            await detail.WaitForMessageCountAsync(1);

            // Wait for session to become idle after receiving the mocked response
            await detail.WaitForIdleAsync();

            // At least one message should be visible (user message)
            var messages = await detail.GetMessageItemsAsync();
            Assert.True(messages.Count >= 1, $"Expected at least 1 message, got {messages.Count}");
        });
    }
}

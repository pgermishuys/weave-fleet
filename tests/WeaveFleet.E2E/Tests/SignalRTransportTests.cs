using System.Text.Json;
using Microsoft.Playwright;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.E2E.Infrastructure;
using WeaveFleet.E2E.Pages;

namespace WeaveFleet.E2E.Tests;

/// <summary>
/// E2E test for SignalR reconnection: losing the connection mid-stream and catching up.
/// </summary>
[Trait("Category", "E2E")]
public sealed class SignalRTransportTests : E2ETestBase,
    IClassFixture<FleetWebApplicationFactory>,
    IClassFixture<PlaywrightFixture>
{
    public SignalRTransportTests(FleetWebApplicationFactory factory, PlaywrightFixture playwright)
        : base(factory, playwright) { }

    /// <summary>
    /// Verifies that the full assistant response arrives after the connection drops mid-stream
    /// and reconnects, with the user prompt shown once.
    /// </summary>
    [Fact]
    public async Task SignalR_DisconnectDuringStreaming_RecoversFullResponse()
    {
        await WithFailureCapture(async () =>
        {
            const string streamingPrompt = "Stream with disconnect";
            const string firstChunk = "Streaming with";
            const string fullResponse = "Streaming with disconnect recovery works.";
            const string messageId = "msg-signalr-disconnect-stream-1";
            const string partId = "part-signalr-disconnect-stream-1";

            ConfigureScenario(builder => builder.WithPromptResponse(response => response
                .AddEvent(MakeHarnessEvent(
                    "session.status",
                    new { sessionId = "_placeholder_", status = new { type = "busy" } }))
                .AddEvent(MakeHarnessEvent(
                    "message.updated",
                    new
                    {
                        info = new
                        {
                            id = messageId,
                            sessionID = "_placeholder_",
                            role = "assistant",
                            time = new { created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
                            agent = "loom",
                        },
                    }),
                    TimeSpan.FromMilliseconds(50))
                .AddEvent(MakeHarnessEvent(
                    "message.part.delta",
                    new
                    {
                        sessionID = "_placeholder_",
                        messageID = messageId,
                        partID = partId,
                        field = "text",
                        delta = firstChunk,
                    }),
                    TimeSpan.FromMilliseconds(100))
                .AddEvent(MakeHarnessEvent(
                    "message.part.delta",
                    new
                    {
                        sessionID = "_placeholder_",
                        messageID = messageId,
                        partID = partId,
                        field = "text",
                        delta = " disconnect",
                    }),
                    TimeSpan.FromMilliseconds(450))
                .AddEvent(MakeHarnessEvent(
                    "message.part.delta",
                    new
                    {
                        sessionID = "_placeholder_",
                        messageID = messageId,
                        partID = partId,
                        field = "text",
                        delta = " recovery works.",
                    }),
                    TimeSpan.FromMilliseconds(450))
                .AddEvent(MakeHarnessEvent(
                    "message.updated",
                    new
                    {
                        info = new
                        {
                            id = messageId,
                            sessionID = "_placeholder_",
                            role = "assistant",
                            time = new
                            {
                                created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                                completed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 1,
                            },
                            agent = "loom",
                        },
                        parts = new[]
                        {
                            new
                            {
                                id = partId,
                                sessionID = "_placeholder_",
                                messageID = messageId,
                                type = "text",
                                text = fullResponse,
                            },
                        },
                    }),
                    TimeSpan.FromMilliseconds(450))
                .AddEvent(MakeHarnessEvent(
                    "session.status",
                    new { sessionId = "_placeholder_", status = new { type = "idle" } }),
                    TimeSpan.FromMilliseconds(50))
                .AddEvent(MakeHarnessEvent(
                    "session.idle",
                    new { sessionId = "_placeholder_" }),
                    TimeSpan.FromMilliseconds(50))));

            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();

            var dialog = await dashboard.ClickNewSessionAsync();
            await dialog.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
            await dialog.SetTitleAsync("SignalR Disconnect During Streaming");

            var detail = await dialog.SubmitAsync();
            await detail.WaitForLoadedAsync();

            var sessionUri = new Uri(Page.Url);
            var sessionId = sessionUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();

            await detail.SendPromptAsync(streamingPrompt, 30_000);

            // Wait for first chunk
            var firstAssistantChunk = detail.GetMessagesByRole("assistant")
                .Filter(new LocatorFilterOptions { HasText = firstChunk });
            await Assertions.Expect(firstAssistantChunk).ToHaveCountAsync(
                1,
                new LocatorAssertionsToHaveCountOptions { Timeout = 15_000 });

            // Drop the connection mid-stream. The UI doesn't show a dropped connection,
            // so check the socket itself.
            await Page.EvaluateAsync("window.__WEAVE_SOCKET_TEST_API.suspend()").ConfigureAwait(false);
            await Page.WaitForFunctionAsync(
                "() => !window.__WEAVE_SOCKET_TEST_API.hasOpenSocket()",
                null,
                new PageWaitForFunctionOptions { Timeout = 10_000 });

            // Wait for the full message to be persisted on the server
            await WaitForRetrievedMessageTextAsync(sessionId, fullResponse, TimeSpan.FromSeconds(30));

            // Resume connection
            await Page.EvaluateAsync("window.__WEAVE_SOCKET_TEST_API.resume()").ConfigureAwait(false);

            // Verify full response is recovered
            await detail.WaitForMessageTextAsync(fullResponse, 30_000);

            // Verify user prompt appears exactly once
            var userPromptMessages = detail.GetMessagesByRole("user")
                .Filter(new LocatorFilterOptions { HasText = streamingPrompt });
            await Assertions.Expect(userPromptMessages).ToHaveCountAsync(
                1,
                new LocatorAssertionsToHaveCountOptions { Timeout = 10_000 });
        });
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private static HarnessEvent MakeHarnessEvent(string type, object payload)
        => new()
        {
            Type = type,
            SessionId = "_placeholder_",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(payload),
        };

    private async Task WaitForRetrievedMessageTextAsync(
        string sessionId,
        string expectedText,
        TimeSpan timeout)
    {
        using var httpClient = new HttpClient { BaseAddress = new Uri(ServerUrl) };
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            using (var response = await httpClient.GetAsync(
                $"/api/sessions/{Uri.EscapeDataString(sessionId)}/messages").ConfigureAwait(false))
            {
                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (body.Contains(expectedText, StringComparison.Ordinal))
                        return;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"Expected retrieved session messages to contain '{expectedText}' within {timeout}.");
    }
}

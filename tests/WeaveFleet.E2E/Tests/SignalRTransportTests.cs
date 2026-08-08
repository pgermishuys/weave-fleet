using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.E2E.Infrastructure;
using WeaveFleet.E2E.Pages;
using WeaveFleet.TestHarness;

namespace WeaveFleet.E2E.Tests;

/// <summary>
/// E2E tests for SignalR transport mode.
/// Validates that all core functionality works with SignalR instead of WebSocket,
/// and tests SignalR-specific reconnection scenarios.
/// 
/// Note: These tests set the 'fleet:transport=signalr' localStorage flag to request
/// SignalR transport. The actual transport used depends on server configuration and
/// client-side transport factory logic. These tests verify that the E2E flow works
/// when SignalR transport is requested.
/// </summary>
[Trait("Category", "E2E")]
[Trait("Lane", "Transport")]
public sealed class SignalRTransportTests : E2ETestBase,
    IClassFixture<FleetWebApplicationFactory>,
    IClassFixture<PlaywrightFixture>
{
    private readonly FleetWebApplicationFactory _factory;

    public SignalRTransportTests(FleetWebApplicationFactory factory, PlaywrightFixture playwright)
        : base(factory, playwright)
    {
        _factory = factory;
    }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        // Enable SignalR transport for all tests in this class
        await Page.Context.AddInitScriptAsync("""
            window.localStorage.setItem('fleet:transport', 'signalr');
            """);
    }

    /// <summary>
    /// Verifies that the golden path works with SignalR transport requested:
    /// create session, send prompt, receive response, session goes idle.
    /// </summary>
    [Fact]
    public async Task SignalR_GoldenPath_CreateSessionAndReceiveResponse()
    {
        await WithFailureCapture(async () =>
        {
            ConfigureScenario(b =>
                b.WithSimpleTextResponse("_placeholder_", "msg-signalr-golden-1", "Hello from SignalR!"));

            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();
            await dashboard.WaitForEmptyStateAsync();

            var dialog = await dashboard.ClickNewSessionAsync();
            await dialog.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
            await dialog.SetTitleAsync("SignalR Golden Path Session");

            var detail = await dialog.SubmitAsync();
            await detail.WaitForLoadedAsync();

            await detail.SendPromptAsync("Hello via SignalR!", 30_000);
            
            // Wait for the user message to appear
            await detail.WaitForMessageTextAsync("Hello via SignalR!", 30_000);
            
            // Wait for session to go idle (response received)
            await detail.WaitForIdleAsync(30_000);

            var messages = await detail.GetMessageItemsAsync();
            messages.Count.ShouldBeGreaterThanOrEqualTo(1);
        });
    }

    /// <summary>
    /// Verifies that SignalR transport can handle streaming responses with deltas.
    /// </summary>
    [Fact]
    public async Task SignalR_StreamingResponse_ReceivesAllDeltas()
    {
        await WithFailureCapture(async () =>
        {
            const string streamingPrompt = "Stream via SignalR";
            const string firstChunk = "Streaming";
            const string fullResponse = "Streaming via SignalR works perfectly.";
            const string messageId = "msg-signalr-stream-1";
            const string partId = "part-signalr-stream-1";

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
                        delta = " via SignalR",
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
                        delta = " works perfectly.",
                    }),
                    TimeSpan.FromMilliseconds(100))
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
                    TimeSpan.FromMilliseconds(100))
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
            await dialog.SetTitleAsync("SignalR Streaming Session");

            var detail = await dialog.SubmitAsync();
            await detail.WaitForLoadedAsync();

            await detail.SendPromptAsync(streamingPrompt, 30_000);

            // Wait for first chunk to appear
            var firstAssistantChunk = detail.GetMessagesByRole("assistant")
                .Filter(new LocatorFilterOptions { HasText = firstChunk });
            await Assertions.Expect(firstAssistantChunk).ToHaveCountAsync(
                1,
                new LocatorAssertionsToHaveCountOptions { Timeout = 15_000 });

            // Wait for full response
            await detail.WaitForMessageTextAsync(fullResponse, 30_000);
            await detail.WaitForIdleAsync(30_000);
        });
    }

    /// <summary>
    /// Verifies that SignalR transport shows disconnected state when connection is lost
    /// and recovers with catch-up when reconnected.
    /// </summary>
    [Fact]
    public async Task SignalR_Reconnect_ShowsDisconnectedStateAndCatchesUp()
    {
        await WithFailureCapture(async () =>
        {
            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();

            var dialog = await dashboard.ClickNewSessionAsync();
            await dialog.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
            await dialog.SetTitleAsync("SignalR Reconnect Session");

            var detail = await dialog.SubmitAsync();
            await detail.WaitForLoadedAsync();

            var sessionUri = new Uri(Page.Url);
            var sessionId = sessionUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();
            var instanceId = GetRequiredQueryValue(sessionUri, "instanceId");

            var tracker = _factory.KestrelServices.GetRequiredService<InstanceTracker>();
            var harness = tracker.Get(instanceId).ShouldBeOfType<TestHarnessSession>();
            var harnessSessionId = harness.InstanceId;

            const string onlineText = "Message before SignalR disconnect";
            const string catchUpText = "Recovered after SignalR reconnect";

            // Push a message while connected
            await PushDurableAssistantMessageAsync(
                harness,
                harnessSessionId,
                sessionId,
                "msg-signalr-online-1",
                onlineText);

            await detail.WaitForMessageTextAsync(onlineText, 10_000);

            // Mark the document so we can verify no reload happened
            var initialDocumentMarker = await Page.EvaluateAsync<string>("""
                () => {
                  window.__signalrResilienceMarker ??= crypto.randomUUID();
                  return window.__signalrResilienceMarker;
                }
                """);

            // Suspend the connection
            await Page.EvaluateAsync("window.__WEAVE_SOCKET_TEST_API?.suspend()")
                .ConfigureAwait(false);

            // Wait for disconnected state
            var disconnectedIndicator = Page.GetByTestId("session-status-indicator");
            await Assertions.Expect(disconnectedIndicator).ToHaveAttributeAsync(
                "data-status",
                "disconnected",
                new LocatorAssertionsToHaveAttributeOptions { Timeout = 10_000 });

            var disconnectedBanner = Page.GetByTestId("session-stopped-banner");
            await Assertions.Expect(disconnectedBanner).ToContainTextAsync(
                "Connection to this session was lost",
                new LocatorAssertionsToContainTextOptions { Timeout = 10_000 });

            // Push a message while disconnected (should be caught up after reconnect)
            await PushDurableAssistantMessageAsync(
                harness,
                harnessSessionId,
                sessionId,
                "msg-signalr-catchup-1",
                catchUpText);

            // Verify message is not visible yet
            await Assertions.Expect(Page.GetByTestId("message-item").Filter(new LocatorFilterOptions { HasText = catchUpText }))
                .ToHaveCountAsync(0, new LocatorAssertionsToHaveCountOptions { Timeout = 1_000 });

            // Resume the connection
            await Page.EvaluateAsync("window.__WEAVE_SOCKET_TEST_API?.resume()")
                .ConfigureAwait(false);

            // Wait for catch-up message to appear
            await detail.WaitForMessageTextAsync(catchUpText, 15_000);

            // Verify we're back to idle state
            await Assertions.Expect(disconnectedIndicator).ToHaveAttributeAsync(
                "data-status",
                "idle",
                new LocatorAssertionsToHaveAttributeOptions { Timeout = 10_000 });
            await Assertions.Expect(disconnectedBanner).ToHaveCountAsync(0, new LocatorAssertionsToHaveCountOptions { Timeout = 10_000 });

            // Verify no page reload occurred
            var finalDocumentMarker = await Page.EvaluateAsync<string>("() => window.__signalrResilienceMarker");
            finalDocumentMarker.ShouldBe(initialDocumentMarker);
        });
    }

    /// <summary>
    /// Verifies that SignalR transport recovers full assistant response when disconnected during streaming.
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

            // Suspend connection during streaming
            await Page.EvaluateAsync("window.__WEAVE_SOCKET_TEST_API?.suspend()").ConfigureAwait(false);

            var disconnectedIndicator = Page.GetByTestId("session-status-indicator");
            await Assertions.Expect(disconnectedIndicator).ToHaveAttributeAsync(
                "data-status",
                "disconnected",
                new LocatorAssertionsToHaveAttributeOptions { Timeout = 10_000 });

            // Wait for the full message to be persisted on the server
            await WaitForRetrievedMessageTextAsync(sessionId, fullResponse, TimeSpan.FromSeconds(30));

            // Resume connection
            await Page.EvaluateAsync("window.__WEAVE_SOCKET_TEST_API?.resume()").ConfigureAwait(false);

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

    /// <summary>
    /// Verifies that SignalR transport can handle multiple rapid reconnects without losing messages.
    /// </summary>
    [Fact]
    public async Task SignalR_MultipleRapidReconnects_NoMessageLoss()
    {
        await WithFailureCapture(async () =>
        {
            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();

            var dialog = await dashboard.ClickNewSessionAsync();
            await dialog.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
            await dialog.SetTitleAsync("SignalR Multiple Reconnects");

            var detail = await dialog.SubmitAsync();
            await detail.WaitForLoadedAsync();

            var sessionUri = new Uri(Page.Url);
            var sessionId = sessionUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();
            var instanceId = GetRequiredQueryValue(sessionUri, "instanceId");

            var tracker = _factory.KestrelServices.GetRequiredService<InstanceTracker>();
            var harness = tracker.Get(instanceId).ShouldBeOfType<TestHarnessSession>();
            var harnessSessionId = harness.InstanceId;

            // Push message 1
            await PushDurableAssistantMessageAsync(harness, harnessSessionId, sessionId, "msg-multi-1", "Message 1");
            await detail.WaitForMessageTextAsync("Message 1", 10_000);

            // First disconnect/reconnect cycle
            await Page.EvaluateAsync("window.__WEAVE_SOCKET_TEST_API?.suspend()").ConfigureAwait(false);
            await Task.Delay(500);
            await PushDurableAssistantMessageAsync(harness, harnessSessionId, sessionId, "msg-multi-2", "Message 2");
            await Page.EvaluateAsync("window.__WEAVE_SOCKET_TEST_API?.resume()").ConfigureAwait(false);
            await detail.WaitForMessageTextAsync("Message 2", 10_000);

            // Second disconnect/reconnect cycle
            await Page.EvaluateAsync("window.__WEAVE_SOCKET_TEST_API?.suspend()").ConfigureAwait(false);
            await Task.Delay(500);
            await PushDurableAssistantMessageAsync(harness, harnessSessionId, sessionId, "msg-multi-3", "Message 3");
            await Page.EvaluateAsync("window.__WEAVE_SOCKET_TEST_API?.resume()").ConfigureAwait(false);
            await detail.WaitForMessageTextAsync("Message 3", 10_000);

            // Third disconnect/reconnect cycle
            await Page.EvaluateAsync("window.__WEAVE_SOCKET_TEST_API?.suspend()").ConfigureAwait(false);
            await Task.Delay(500);
            await PushDurableAssistantMessageAsync(harness, harnessSessionId, sessionId, "msg-multi-4", "Message 4");
            await Page.EvaluateAsync("window.__WEAVE_SOCKET_TEST_API?.resume()").ConfigureAwait(false);
            await detail.WaitForMessageTextAsync("Message 4", 10_000);

            // Verify all messages are present
            await detail.WaitForMessageTextAsync("Message 1", 5_000);
            await detail.WaitForMessageTextAsync("Message 2", 5_000);
            await detail.WaitForMessageTextAsync("Message 3", 5_000);
            await detail.WaitForMessageTextAsync("Message 4", 5_000);

            var messages = await detail.GetMessageItemsAsync();
            messages.Count.ShouldBeGreaterThanOrEqualTo(4);
        });
    }

    /// <summary>
    /// Verifies that SignalR transport works correctly when switching from WebSocket to SignalR mid-session.
    /// This tests the transport toggle functionality.
    /// </summary>
    [Fact]
    public async Task SignalR_TransportToggle_WorksAfterPageReload()
    {
        await WithFailureCapture(async () =>
        {
            // Start with WebSocket (default)
            ConfigureScenario(b =>
                b.WithSimpleTextResponse("_placeholder_", "msg-toggle-1", "WebSocket message"));

            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();

            var dialog = await dashboard.ClickNewSessionAsync();
            await dialog.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
            await dialog.SetTitleAsync("Transport Toggle Session");

            var detail = await dialog.SubmitAsync();
            await detail.WaitForLoadedAsync();

            await detail.SendPromptAsync("Test WebSocket", 30_000);
            await detail.WaitForMessageTextAsync("WebSocket message", 30_000);

            // Switch to SignalR
            await Page.EvaluateAsync("window.localStorage.setItem('fleet:transport', 'signalr')");

            // Reload the page to activate SignalR transport
            await Page.ReloadAsync();
            await detail.WaitForLoadedAsync();

            // Verify previous messages are still visible
            await detail.WaitForMessageTextAsync("WebSocket message", 10_000);

            // Send another prompt with SignalR
            ConfigureScenario(b =>
                b.WithSimpleTextResponse("_placeholder_", "msg-toggle-2", "SignalR message"));

            await detail.SendPromptAsync("Test SignalR", 30_000);
            await detail.WaitForMessageTextAsync("SignalR message", 30_000);

            // Verify both messages are present
            var messages = await detail.GetMessageItemsAsync();
            messages.Count.ShouldBeGreaterThanOrEqualTo(2);
        });
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private static async Task PushDurableAssistantMessageAsync(
        TestHarnessSession harness,
        string harnessSessionId,
        string fleetSessionId,
        string messageId,
        string text)
    {
        await harness.PushEventAsync(new HarnessEvent
        {
            Type = "message.updated",
            SessionId = harnessSessionId,
            FleetSessionId = fleetSessionId,
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new
            {
                info = new
                {
                    id = messageId,
                    sessionID = harnessSessionId,
                    role = "assistant",
                    time = new { created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
                    agent = "loom",
                }
            })
        });

        await harness.PushEventAsync(new HarnessEvent
        {
            Type = "message.part.updated",
            SessionId = harnessSessionId,
            FleetSessionId = fleetSessionId,
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new
            {
                part = new
                {
                    id = $"part-{messageId}",
                    messageID = messageId,
                    sessionID = harnessSessionId,
                    type = "text",
                    text,
                }
            })
        });
    }

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

    private static string GetRequiredQueryValue(Uri uri, string key)
    {
        var value = System.Web.HttpUtility.ParseQueryString(uri.Query)[key];
        value.ShouldNotBeNullOrWhiteSpace();
        return value;
    }
}

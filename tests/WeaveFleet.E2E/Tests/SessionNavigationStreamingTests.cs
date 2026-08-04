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
/// E2E regression test for session navigation during streaming.
/// Guards the fix in client/src/composables/use-signalr-socket.ts (per-topic promise queue sequencing).
/// 
/// Bug: Navigating session A → B → A while an agent response was streaming left the client detached
/// from the SignalR group `session:A`, so no live events arrived and the agent appeared idle until
/// page refresh. The race was a fire-and-forget UnsubscribeFromSessionAsync overtaking a re-subscribe
/// in browser-side scheduling.
/// 
/// Fix: Per-topic operation queue (topicOperationQueues) ensures subscribe/unsubscribe operations
/// are sequenced, preventing the race.
/// </summary>
[Trait("Category", "E2E")]
[Trait("Lane", "Transport")]
public sealed class SessionNavigationStreamingTests : E2ETestBase,
    IClassFixture<FleetWebApplicationFactory>,
    IClassFixture<PlaywrightFixture>
{
    private readonly FleetWebApplicationFactory _factory;

    public SessionNavigationStreamingTests(FleetWebApplicationFactory factory, PlaywrightFixture playwright)
        : base(factory, playwright)
    {
        _factory = factory;
    }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        // Enable SignalR transport for this test
        await Page.Context.AddInitScriptAsync("""
            window.localStorage.setItem('fleet:transport', 'signalr');
            """);
    }

    /// <summary>
    /// Regression test: Navigate A → B → A while A is streaming, verify live events continue
    /// to arrive in A without page refresh.
    /// 
    /// Steps:
    /// 1. Create two sessions (A and B).
    /// 2. Open session A, send a prompt that triggers a slow streaming response (multiple deltas over ~2s).
    /// 3. After the first streamed content appears, navigate to session B.
    /// 4. Navigate back to session A while A's response is still streaming.
    /// 5. Assert WITHOUT page refresh that:
    ///    a) New streaming content continues to appear in A's conversation (content grows after navigation back).
    ///    b) The session busy indicator shows the agent as busy while streaming, then transitions to idle when done.
    /// 6. Final assertion: the completed response text is fully present without any reload.
    /// </summary>
    [Fact]
    public async Task NavigateAwayAndBackDuringStreaming_ContinuesReceivingLiveEvents()
    {
        await WithFailureCapture(async () =>
        {
            // ── Setup: Create two sessions ──────────────────────────────────────

            ConfigureScenario(_ => { });

            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();

            // Create session A
            var dialogA = await dashboard.ClickNewSessionAsync();
            await dialogA.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
            await dialogA.SetTitleAsync("Session A");

            var detailA = await dialogA.SubmitAsync();
            await detailA.WaitForLoadedAsync();

            var sessionUriA = new Uri(Page.Url);
            var sessionIdA = sessionUriA.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();
            var instanceIdA = GetRequiredQueryValue(sessionUriA, "instanceId");

            // Create session B
            await Page.GotoAsync("/");
            await dashboard.WaitForLoadedAsync();

            var dialogB = await dashboard.ClickNewSessionAsync();
            await dialogB.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
            await dialogB.SetTitleAsync("Session B");

            var detailB = await dialogB.SubmitAsync();
            await detailB.WaitForLoadedAsync();

            var sessionUriB = new Uri(Page.Url);
            var sessionIdB = sessionUriB.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();
            var instanceIdB = GetRequiredQueryValue(sessionUriB, "instanceId");

            // ── Step 1: Navigate to session A and start a slow streaming response ──

            await detailA.GotoAsync(sessionIdA, instanceIdA);

            // Verify session is idle before we start
            await detailA.WaitForIdleAsync(10_000);

            var tracker = _factory.KestrelServices.GetRequiredService<InstanceTracker>();
            var harnessA = tracker.Get(instanceIdA).ShouldBeOfType<TestHarnessSession>();
            var harnessSessionIdA = harnessA.InstanceId;

            const string messageId = "msg-streaming-a";
            const string partId = "part-streaming-a";
            const string firstChunk = "Streaming response";
            const string secondChunk = " from session A";
            const string thirdChunk = " continues after navigation.";
            const string fullResponse = firstChunk + secondChunk + thirdChunk;

            // Mark the document so we can verify no reload happened
            var initialDocumentMarker = await Page.EvaluateAsync<string>("""
                () => {
                  window.__navigationStreamingMarker ??= crypto.randomUUID();
                  return window.__navigationStreamingMarker;
                }
                """);

            // Push session.status(busy)
            await harnessA.PushEventAsync(MakeHarnessEvent(
                harnessSessionIdA,
                sessionIdA,
                "session.status",
                new { sessionId = harnessSessionIdA, status = new { type = "busy" } }));

            // Wait for session to become busy
            await detailA.WaitForBusyAsync(10_000);

            // Push message.updated (assistant message created)
            await harnessA.PushEventAsync(MakeHarnessEvent(
                harnessSessionIdA,
                sessionIdA,
                "message.updated",
                new
                {
                    info = new
                    {
                        id = messageId,
                        sessionID = harnessSessionIdA,
                        role = "assistant",
                        time = new { created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
                        agent = "loom",
                    },
                }));

            // Push first chunk as message.part.updated (not delta, since we're building the full text)
            await harnessA.PushEventAsync(MakeHarnessEvent(
                harnessSessionIdA,
                sessionIdA,
                "message.part.updated",
                new
                {
                    part = new
                    {
                        id = partId,
                        messageID = messageId,
                        sessionID = harnessSessionIdA,
                        type = "text",
                        text = firstChunk,
                    }
                }));

            // Wait for first chunk to appear
            await detailA.WaitForMessageTextAsync(firstChunk, 10_000);

            // ── Step 2: Navigate to session B (client-side, no reload) ──────────

            // Use Vue Router's push method for client-side navigation
            await Page.EvaluateAsync($@"
                window.$router?.push({{
                    path: '/sessions/{Uri.EscapeDataString(sessionIdB)}',
                    query: {{ instanceId: '{Uri.EscapeDataString(instanceIdB)}' }}
                }})
            ");
            await detailB.WaitForLoadedAsync();
            await Task.Delay(200); // Allow navigation to settle

            // ── Step 3: Push second chunk while we're on session B ──────────────

            await harnessA.PushEventAsync(MakeHarnessEvent(
                harnessSessionIdA,
                sessionIdA,
                "message.part.updated",
                new
                {
                    part = new
                    {
                        id = partId,
                        messageID = messageId,
                        sessionID = harnessSessionIdA,
                        type = "text",
                        text = firstChunk + secondChunk,
                    }
                }));

            await Task.Delay(200);

            // ── Step 4: Navigate back to session A (client-side, no reload) ─────

            // Use Vue Router's push method for client-side navigation
            await Page.EvaluateAsync($@"
                window.$router?.push({{
                    path: '/sessions/{Uri.EscapeDataString(sessionIdA)}',
                    query: {{ instanceId: '{Uri.EscapeDataString(instanceIdA)}' }}
                }})
            ");
            await detailA.WaitForLoadedAsync();

            // ── Step 5: Verify session A is still busy and receiving live events ──

            // Session should still be busy
            var statusAfterReturn = await detailA.GetStatusAsync();
            statusAfterReturn.ShouldBe("working", "Session A should still be busy after navigating back");

            // The second chunk should be present (caught up via snapshot or live event)
            await detailA.WaitForMessageTextAsync(firstChunk + secondChunk, 10_000);

            // ── Step 6: Push third chunk and verify it arrives live ─────────────

            await harnessA.PushEventAsync(MakeHarnessEvent(
                harnessSessionIdA,
                sessionIdA,
                "message.part.updated",
                new
                {
                    part = new
                    {
                        id = partId,
                        messageID = messageId,
                        sessionID = harnessSessionIdA,
                        type = "text",
                        text = fullResponse,
                    }
                }));

            // Wait for the third chunk to appear (proves live events are flowing)
            await detailA.WaitForMessageTextAsync(fullResponse, 10_000);

            // ── Step 7: Complete the response and verify idle transition ────────

            // Push message.updated (final state with all parts)
            await harnessA.PushEventAsync(MakeHarnessEvent(
                harnessSessionIdA,
                sessionIdA,
                "message.updated",
                new
                {
                    info = new
                    {
                        id = messageId,
                        sessionID = harnessSessionIdA,
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
                            sessionID = harnessSessionIdA,
                            messageID = messageId,
                            type = "text",
                            text = fullResponse,
                        },
                    },
                }));

            // Push session.status(idle)
            await harnessA.PushEventAsync(MakeHarnessEvent(
                harnessSessionIdA,
                sessionIdA,
                "session.status",
                new { sessionId = harnessSessionIdA, status = new { type = "idle" } }));

            // Push session.idle
            await harnessA.PushEventAsync(MakeHarnessEvent(
                harnessSessionIdA,
                sessionIdA,
                "session.idle",
                new { sessionId = harnessSessionIdA }));

            // Wait for session to become idle
            await detailA.WaitForIdleAsync(10_000);

            // ── Step 8: Final assertions ─────────────────────────────────────────

            // Full response text should be present
            await detailA.WaitForMessageTextAsync(fullResponse, 5_000);

            // Verify no page reload occurred
            var finalDocumentMarker = await Page.EvaluateAsync<string>("() => window.__navigationStreamingMarker");
            finalDocumentMarker.ShouldBe(initialDocumentMarker, "Page should not have reloaded");

            // Verify session is idle
            var finalStatus = await detailA.GetStatusAsync();
            finalStatus.ShouldBe("idle", "Session A should be idle after response completes");
        });
    }

    /// <summary>
    /// Stress test: Rapidly navigate A → B → A many times with zero delays during streaming.
    /// Verifies the queue handles rapid navigation without losing subscription.
    /// 
    /// Without the fix (queueTopicOperation), this test should fail or be flaky because:
    /// - Rapid navigation causes overlapping subscribe/unsubscribe calls
    /// - Fire-and-forget unsubscribe can overtake a subsequent subscribe
    /// - Client ends up detached from the SignalR group
    /// - Live events no longer arrive
    /// </summary>
    [Fact]
    public async Task RapidNavigationDuringStreaming_MaintainsSubscription()
    {
        await WithFailureCapture(async () =>
        {
            // ── Setup: Create two sessions ──────────────────────────────────────

            ConfigureScenario(_ => { });

            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();

            // Create session A
            var dialogA = await dashboard.ClickNewSessionAsync();
            await dialogA.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
            await dialogA.SetTitleAsync("Session A Rapid");

            var detailA = await dialogA.SubmitAsync();
            await detailA.WaitForLoadedAsync();

            var sessionUriA = new Uri(Page.Url);
            var sessionIdA = sessionUriA.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();
            var instanceIdA = GetRequiredQueryValue(sessionUriA, "instanceId");

            // Create session B
            await Page.GotoAsync("/");
            await dashboard.WaitForLoadedAsync();

            var dialogB = await dashboard.ClickNewSessionAsync();
            await dialogB.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
            await dialogB.SetTitleAsync("Session B Rapid");

            var detailB = await dialogB.SubmitAsync();
            await detailB.WaitForLoadedAsync();

            var sessionUriB = new Uri(Page.Url);
            var sessionIdB = sessionUriB.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();
            var instanceIdB = GetRequiredQueryValue(sessionUriB, "instanceId");

            var tracker = _factory.KestrelServices.GetRequiredService<InstanceTracker>();
            var harnessA = tracker.Get(instanceIdA).ShouldBeOfType<TestHarnessSession>();
            var harnessSessionIdA = harnessA.InstanceId;

            // ── Run multiple iterations to increase probability of hitting the race ──

            const int iterations = 3;
            for (var iteration = 0; iteration < iterations; iteration++)
            {
                // Navigate to session A using router (client-side navigation)
                await Page.EvaluateAsync($@"
                    window.$router?.push({{
                        path: '/sessions/{Uri.EscapeDataString(sessionIdA)}',
                        query: {{ instanceId: '{Uri.EscapeDataString(instanceIdA)}' }}
                    }})
                ");
                await detailA.WaitForLoadedAsync();
                
                // Wait for session to be idle (SignalR subscription established)
                await detailA.WaitForIdleAsync(10_000);

                var messageId = $"msg-rapid-{iteration}";
                var partId = $"part-rapid-{iteration}";

                // Push session.status(busy)
                await harnessA.PushEventAsync(MakeHarnessEvent(
                    harnessSessionIdA,
                    sessionIdA,
                    "session.status",
                    new { sessionId = harnessSessionIdA, status = new { type = "busy" } }));

                // Wait for busy status to be reflected
                await detailA.WaitForBusyAsync(5_000);

                // Push message.updated
                await harnessA.PushEventAsync(MakeHarnessEvent(
                    harnessSessionIdA,
                    sessionIdA,
                    "message.updated",
                    new
                    {
                        info = new
                        {
                            id = messageId,
                            sessionID = harnessSessionIdA,
                            role = "assistant",
                            time = new { created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
                            agent = "loom",
                        },
                    }));

                // Push initial chunk
                var initialText = $"Iteration {iteration} chunk 1";
                await harnessA.PushEventAsync(MakeHarnessEvent(
                    harnessSessionIdA,
                    sessionIdA,
                    "message.part.updated",
                    new
                    {
                        part = new
                        {
                            id = partId,
                            messageID = messageId,
                            sessionID = harnessSessionIdA,
                            type = "text",
                            text = initialText,
                        }
                    }));

                await detailA.WaitForMessageTextAsync(initialText, 10_000);

                // ── Rapidly navigate A → B → A many times with ZERO delays ──────

                const int rapidCycles = 20;
                for (var i = 0; i < rapidCycles; i++)
                {
                    // Navigate to B (triggers unsubscribe from A)
                    await Page.EvaluateAsync($@"
                        window.$router?.push({{
                            path: '/sessions/{Uri.EscapeDataString(sessionIdB)}',
                            query: {{ instanceId: '{Uri.EscapeDataString(instanceIdB)}' }}
                        }})
                    ");
                    
                    // Immediately navigate back to A (triggers subscribe to A)
                    // Without queueing, this subscribe can be sent before the unsubscribe completes
                    await Page.EvaluateAsync($@"
                        window.$router?.push({{
                            path: '/sessions/{Uri.EscapeDataString(sessionIdA)}',
                            query: {{ instanceId: '{Uri.EscapeDataString(instanceIdA)}' }}
                        }})
                    ");
                }

                // Wait for navigation to settle
                await Task.Delay(100);

                // ── Immediately push an event and assert it arrives live ────────

                var finalText = $"Iteration {iteration} chunk 2";
                await harnessA.PushEventAsync(MakeHarnessEvent(
                    harnessSessionIdA,
                    sessionIdA,
                    "message.part.updated",
                    new
                    {
                        part = new
                        {
                            id = partId,
                            messageID = messageId,
                            sessionID = harnessSessionIdA,
                            type = "text",
                            text = finalText,
                        }
                    }));

                // This should arrive via live events within 3s
                // If the subscription was lost due to the race, this will timeout
                await detailA.WaitForMessageTextAsync(finalText, 3_000);

                // Verify subscription is active via test API
                var hasSubscription = await Page.EvaluateAsync<bool>($@"
                    window.__WEAVE_SOCKET_TEST_API?.hasV2Subscriptions() ?? false
                ");
                hasSubscription.ShouldBeTrue($"Iteration {iteration}: SignalR subscription should be active after rapid navigation");

                // Complete the response
                await harnessA.PushEventAsync(MakeHarnessEvent(
                    harnessSessionIdA,
                    sessionIdA,
                    "session.status",
                    new { sessionId = harnessSessionIdA, status = new { type = "idle" } }));

                await detailA.WaitForIdleAsync(5_000);
            }
        });
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private static HarnessEvent MakeHarnessEvent(
        string harnessSessionId,
        string fleetSessionId,
        string type,
        object payload)
        => new()
        {
            Type = type,
            SessionId = harnessSessionId,
            FleetSessionId = fleetSessionId,
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(payload),
        };

    private static string GetRequiredQueryValue(Uri uri, string key)
    {
        var value = System.Web.HttpUtility.ParseQueryString(uri.Query)[key];
        value.ShouldNotBeNullOrWhiteSpace();
        return value;
    }
}

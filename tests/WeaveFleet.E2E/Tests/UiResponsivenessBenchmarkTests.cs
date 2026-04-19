using System.Text.Json;
using Microsoft.Playwright;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.E2E.Infrastructure;
using WeaveFleet.E2E.Pages;
using WeaveFleet.TestHarness;
using Xunit.Abstractions;

namespace WeaveFleet.E2E.Tests;

/// <summary>
/// UI responsiveness benchmark tests.
/// Measures user-perceived interaction latencies under idle and loaded conditions.
/// Tagged with both [Trait("Category", "E2E")] and [Trait("Category", "Benchmark")]
/// so benchmark coverage remains identifiable as E2E while normal CI filters can exclude it.
/// </summary>
[Trait("Category", "E2E")]
[Trait("Category", "Benchmark")]
public sealed class UiResponsivenessBenchmarkTests : BenchmarkTestBase,
    IClassFixture<FleetWebApplicationFactory>,
    IClassFixture<PlaywrightFixture>
{
    public UiResponsivenessBenchmarkTests(
        FleetWebApplicationFactory factory,
        PlaywrightFixture playwright,
        ITestOutputHelper output)
        : base(factory, playwright, output) { }

    /// <summary>
    /// Baseline scenario: 10 idle sessions, no background traffic.
    /// Collects metrics without asserting thresholds.
    /// </summary>
    [Fact]
    public async Task Baseline_IdleSessions_UiResponsiveness()
    {
        await WithFailureCapture(async () =>
        {
            SetBenchmarkContext("baseline-idle", nameof(Baseline_IdleSessions_UiResponsiveness));
            ConfigureScenario(_ => { });
            var sessions = await CreateSessionsAsync(10);

            // Push one initial message per session so activity streams have content
            await SeedInitialMessagesAsync(sessions);

            // Wait for dashboard to reflect all sessions
            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();
            await WaitForCreatedSessionsVisibleAsync(dashboard, sessions);

            // Run the interaction script with NO background traffic
            await RunInteractionScriptAsync(sessions, iterations: 3);

            // Report only — no strict assertions on baseline
            Output.WriteLine("=== BASELINE (idle sessions) ===");
            Output.WriteLine(Metrics.ToReport());
        });
    }

    /// <summary>
    /// Loaded scenario: 10 sessions with continuous background event traffic (~100 events/sec total).
    /// Asserts conservative p95 and max thresholds for each interaction.
    /// </summary>
    [Fact]
    public async Task Loaded_ActiveTraffic_UiResponsiveness()
    {
        await WithFailureCapture(async () =>
        {
            SetBenchmarkContext("loaded-active-traffic", nameof(Loaded_ActiveTraffic_UiResponsiveness));
            ConfigureScenario(_ => { });
            var sessions = await CreateSessionsAsync(10);

            // Seed initial messages
            await SeedInitialMessagesAsync(sessions);

            // Wait for dashboard to reflect all sessions
            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();
            await WaitForCreatedSessionsVisibleAsync(dashboard, sessions);

            // Start continuous background traffic (~10 events/sec per session = 100 events/sec total)
            using var trafficCts = StartBackgroundTraffic(sessions, interval: TimeSpan.FromMilliseconds(100));

            // Let traffic warm up
            await Task.Delay(1000);

            // Run the interaction script under load with enough samples
            // for p95 and max to be meaningfully distinct.
            await RunInteractionScriptAsync(sessions, iterations: 20);

            // Stop traffic
            trafficCts.Cancel();

            // Report and assert
            Output.WriteLine("=== LOADED (10 sessions, continuous traffic) ===");
            Output.WriteLine(Metrics.ToReport());

            // Conservative thresholds for real-browser E2E runs on shared/dev machines.
            // Keep search/card interactions tight, but allow more headroom for full
            // session navigation and activity-stream readiness under sustained load.
            Metrics.AssertP95Below("session_card_open_ms", 500);
            Metrics.AssertP95Below("session_switch_ms", 900);
            Metrics.AssertP95Below("activity_stream_ready_ms", 900);
            Metrics.AssertMaxBelow("session_card_open_ms", 1000);
            Metrics.AssertMaxBelow("session_switch_ms", 1000);
            Metrics.AssertMaxBelow("activity_stream_ready_ms", 1000);
        });
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Runs the standard interaction script measuring three interactions.
    /// Repeated <paramref name="iterations"/> times per interaction.
    /// </summary>
    private async Task RunInteractionScriptAsync(
        IReadOnlyList<(string SessionId, string InstanceId)> sessions,
        int iterations = 3)
    {
        var dashboard = new FleetDashboardPage(Page);

        // ── 1. Session card click to detail view ──────────────────────────────
        for (var i = 0; i < iterations; i++)
        {
            var targetSession = sessions[i % sessions.Count];
            await dashboard.GotoAsync();
            await WaitForCreatedSessionsVisibleAsync(dashboard, sessions);

            await MeasureAsync("session_card_open_ms",
                action: () => dashboard.GetSessionCard(targetSession.SessionId).ClickAsync(),
                waitForCondition: () => WaitForSessionDetailReadyAsync(targetSession.SessionId));
        }

        // ── 2. Session switching latency ──────────────────────────────────────
        for (var i = 0; i < iterations; i++)
        {
            var fromSession = sessions[i % sessions.Count];
            var toSession = sessions[(i + 1) % sessions.Count];

            // Navigate to source session first
            var detailPage = new SessionDetailPage(Page);
            await detailPage.GotoAsync(fromSession.SessionId, fromSession.InstanceId);

            await MeasureAsync("session_switch_ms",
                action: () => Page.GotoAsync(
                    $"/sessions/{Uri.EscapeDataString(toSession.SessionId)}?instanceId={Uri.EscapeDataString(toSession.InstanceId)}"),
                waitForCondition: () => WaitForSessionDetailReadyAsync(toSession.SessionId));
        }

        // ── 3. Activity stream ready latency ──────────────────────────────────
        for (var i = 0; i < iterations; i++)
        {
            var targetSession = sessions[i % sessions.Count];

            // Go to dashboard first to clear session cache
            await dashboard.GotoAsync();
            await Task.Delay(100);

            await MeasureAsync("activity_stream_ready_ms",
                action: () => Page.GotoAsync(
                    $"/sessions/{Uri.EscapeDataString(targetSession.SessionId)}?instanceId={Uri.EscapeDataString(targetSession.InstanceId)}"),
                waitForCondition: () => WaitForSessionDetailReadyAsync(targetSession.SessionId));
        }
    }

    private async Task WaitForSessionDetailReadyAsync(string sessionId)
    {
        await Page.WaitForURLAsync(
            url => url.Contains($"/sessions/{Uri.EscapeDataString(sessionId)}", StringComparison.Ordinal));

        await Page.GetByTestId("activity-stream")
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

        await Page.GetByTestId("prompt-input")
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
    }

    private static async Task WaitForCreatedSessionsVisibleAsync(
        FleetDashboardPage dashboard,
        IReadOnlyList<(string SessionId, string InstanceId)> sessions)
    {
        for (var i = 0; i < sessions.Count; i++)
        {
            await dashboard.GetSessionCard(sessions[i].SessionId)
                .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        }
    }


    /// <summary>
    /// Push a message.updated + message.part.updated pair per session so each session
    /// has at least one message visible in the activity stream.
    /// </summary>
    private async Task SeedInitialMessagesAsync(
        IReadOnlyList<(string SessionId, string InstanceId)> sessions)
    {
        var tracker = GetInstanceTracker();
        foreach (var (sessionId, instanceId) in sessions)
        {
            var instance = (TestHarnessSession)tracker.Get(instanceId)!;
            var msgId = $"seed-msg-{sessionId}";

            await instance.PushEventAsync(new HarnessEvent
            {
                Type = "message.updated",
                SessionId = instanceId,
                FleetSessionId = sessionId,
                Timestamp = DateTimeOffset.UtcNow,
                Payload = JsonSerializer.SerializeToElement(new
                {
                    info = new { id = msgId, sessionID = instanceId, role = "assistant" }
                })
            });

            await instance.PushEventAsync(new HarnessEvent
            {
                Type = "message.part.updated",
                SessionId = instanceId,
                FleetSessionId = sessionId,
                Timestamp = DateTimeOffset.UtcNow,
                Payload = JsonSerializer.SerializeToElement(new
                {
                    part = new
                    {
                        id = $"seed-part-{sessionId}",
                        sessionID = instanceId,
                        messageID = msgId,
                        type = "text",
                        text = $"Initial message for session {sessionId}"
                    }
                })
            });
        }

        // Brief wait for events to propagate through the relay
        await Task.Delay(500);
    }
}

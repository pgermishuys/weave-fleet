using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.E2E.Infrastructure;
using WeaveFleet.E2E.Pages;
using WeaveFleet.Infrastructure.Data.Repositories;
using WeaveFleet.TestHarness;
using WeaveFleet.Testing.Fixtures;

namespace WeaveFleet.E2E.Tests;

/// <summary>
/// Playwright E2E tests for delegation replay using the shared <see cref="DelegationReplayFixture"/>.
/// Covers delegation link UI, model labels, parent busy-state transitions, and WebSocket delivery.
/// </summary>
[Trait("Category", "E2E")]
[Trait("Lane", "Workflow")]
public sealed class DelegationReplayE2ETests : E2ETestBase,
    IClassFixture<FleetWebApplicationFactory>,
    IClassFixture<PlaywrightFixture>
{
    private readonly FleetWebApplicationFactory _factory;

    public DelegationReplayE2ETests(FleetWebApplicationFactory factory, PlaywrightFixture playwright)
        : base(factory, playwright)
    {
        _factory = factory;
    }

    // -----------------------------------------------------------------------
    // Shared setup helper
    // -----------------------------------------------------------------------

    private sealed record DelegationScenario(
        string ParentSessionId,
        string ParentInstanceId,
        string ParentHarnessSessionId,
        string ParentToolCallId,
        string ChildHarnessSessionId,
        string ChildSessionId,
        string ChildInstanceId,
        TestHarnessSession ParentHarness,
        TestHarnessSession ChildHarness);

    private async Task<DelegationScenario> SeedDelegationScenarioAsync()
    {
        ConfigureScenario(_ => { });

        var now = DateTimeOffset.UtcNow;
        var childHarnessSessionId = DelegationReplayFixture.ChildSessionId + "-" + Guid.NewGuid().ToString("N")[..8];
        var parentToolCallId = DelegationReplayFixture.ParentToolCallId;

        var dashboard = new FleetDashboardPage(Page);
        await dashboard.GotoAsync();

        var dialog = await dashboard.ClickNewSessionAsync();
        await dialog.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
        await dialog.SetTitleAsync("Parent Delegation Session");

        var detail = await dialog.SubmitAsync();
        await detail.WaitForLoadedAsync();

        var parentSessionUri = new Uri(Page.Url);
        var parentSessionId = parentSessionUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();
        var parentInstanceId = GetRequiredQueryValue(parentSessionUri, "instanceId");

        var connFactory = _factory.KestrelServices.GetRequiredService<IDbConnectionFactory>();
        var tracker = _factory.KestrelServices.GetRequiredService<InstanceTracker>();
        var userContext = new TestUserContext("local-user");
        var parentHarness = tracker.Get(parentInstanceId).ShouldBeOfType<TestHarnessSession>();
        var parentHarnessSessionId = parentHarness.InstanceId;

        var messageRepo = new MessageRepository(connFactory, userContext);
        await messageRepo.UpsertAsync(
            MessagePersistenceService.ToPersistedMessage(
                parentSessionId,
                new HarnessMessage
                {
                    Id = $"msg-parent-{Guid.NewGuid():N}",
                    Role = "assistant",
                    Parts =
                    [
                        new ToolUsePart(
                            ToolCallId: parentToolCallId,
                            ToolName: "task",
                            Arguments: JsonSerializer.SerializeToElement(new
                            {
                                subagent_type = DelegationReplayFixture.ChildAgent,
                            }),
                            State: ToolUseState.Running),
                    ],
                    Timestamp = now,
                    Agent = DelegationReplayFixture.ParentAgent,
                    ModelId = DelegationReplayFixture.ParentModelId,
                }));

        string childSessionId;
        string childInstanceId;
        TestHarnessSession childHarness;

        using (var scope = _factory.KestrelServices.CreateScope())
        {
            var delegationService = scope.ServiceProvider.GetRequiredService<DelegationService>();
            var sessionOrchestrator = scope.ServiceProvider.GetRequiredService<SessionOrchestrator>();

            await delegationService.HandleDelegationDetectedAsync(parentSessionId, parentToolCallId,
                DelegationReplayFixture.ChildAgent);

            var childSessionResult = await sessionOrchestrator.EnsureDelegatedChildSessionAsync(
                parentSessionId,
                childHarnessSessionId,
                DelegationReplayFixture.ChildTitle);
            childSessionResult.IsSuccess.ShouldBeTrue(
                childSessionResult.IsFailure ? childSessionResult.Error.ToString() : null);

            var childSession = childSessionResult.Value;
            childSessionId = childSession.Id;
            childInstanceId = childSession.InstanceId;

            await delegationService.HandleChildLinkedAsync(
                parentSessionId,
                parentToolCallId,
                childSession.Id);

            childHarness = tracker.Get(childSession.InstanceId).ShouldBeOfType<TestHarnessSession>();
        }

        await Page.GotoAsync("/");
        await dashboard.WaitForLoadedAsync();
        await detail.GotoAsync(parentSessionId, parentInstanceId);

        return new DelegationScenario(
            parentSessionId,
            parentInstanceId,
            parentHarnessSessionId,
            parentToolCallId,
            childHarnessSessionId,
            childSessionId,
            childInstanceId,
            parentHarness,
            childHarness);
    }

    // -----------------------------------------------------------------------
    // Task 10 — Delegation link visible, navigates to child, breadcrumb present
    // -----------------------------------------------------------------------

    [Fact]
    public async Task DelegationReplayE2E_DelegationLink_VisibleAndNavigatesToChild()
    {
        await WithFailureCapture(async () =>
        {
            var scenario = await SeedDelegationScenarioAsync();
            var detail = new SessionDetailPage(Page);
            await detail.GotoAsync(scenario.ParentSessionId, scenario.ParentInstanceId);

            // Delegation link should be visible (seeded via message repo above)
            var delegationLink = Page.GetByTestId("delegation-link");
            await Assertions.Expect(delegationLink).ToBeVisibleAsync(
                new LocatorAssertionsToBeVisibleOptions { Timeout = 10_000 });

            // It should show "Running" or similar status
            var statusEl = Page.GetByTestId("delegation-link-status");
            await Assertions.Expect(statusEl).ToBeVisibleAsync();

            // Click should navigate to child session
            await delegationLink.ClickAsync();

            await Assertions.Expect(Page)
                .ToHaveURLAsync(
                    new System.Text.RegularExpressions.Regex(
                        $"/sessions/{System.Text.RegularExpressions.Regex.Escape(scenario.ChildSessionId)}"),
                    new PageAssertionsToHaveURLOptions { Timeout = 5_000 });

            await detail.WaitForLoadedAsync();

            // Breadcrumb back to parent should be visible
            var breadcrumbLink = Page.Locator(
                $"a[href*=\"/sessions/{Uri.EscapeDataString(scenario.ParentSessionId)}\"]").Last;
            await Assertions.Expect(breadcrumbLink).ToContainTextAsync("Parent Delegation Session",
                new LocatorAssertionsToContainTextOptions { Timeout = 5_000 });
        });
    }

    // -----------------------------------------------------------------------
    // Task 11 — Child session shows child model label
    // -----------------------------------------------------------------------

    [Fact]
    public async Task DelegationReplayE2E_ChildSession_ShowsChildModelId()
    {
        await WithFailureCapture(async () =>
        {
            var scenario = await SeedDelegationScenarioAsync();
            var detail = new SessionDetailPage(Page);

            // Navigate to child session
            await detail.GotoAsync(scenario.ChildSessionId, scenario.ChildInstanceId);

            // Push child assistant message with model info via the fixture's child events
            await scenario.ChildHarness.PushEventAsync(new HarnessEvent
            {
                Type = EventTypes.MessageUpdated,
                SessionId = scenario.ChildHarnessSessionId,
                FleetSessionId = scenario.ChildSessionId,
                Timestamp = DateTimeOffset.UtcNow,
                Payload = JsonSerializer.SerializeToElement(new
                {
                    info = new
                    {
                        id = "child-msg-model-1",
                        sessionID = scenario.ChildHarnessSessionId,
                        role = "assistant",
                        agent = DelegationReplayFixture.ChildAgent,
                        modelID = DelegationReplayFixture.ChildModelId,
                        providerID = DelegationReplayFixture.ChildProviderId,
                        time = new { created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
                    },
                }),
            });
            await scenario.ChildHarness.PushEventAsync(new HarnessEvent
            {
                Type = EventTypes.MessagePartUpdated,
                SessionId = scenario.ChildHarnessSessionId,
                FleetSessionId = scenario.ChildSessionId,
                Timestamp = DateTimeOffset.UtcNow,
                Payload = JsonSerializer.SerializeToElement(new
                {
                    part = new
                    {
                        id = "child-part-model-1",
                        messageID = "child-msg-model-1",
                        sessionID = scenario.ChildHarnessSessionId,
                        type = "text",
                        text = $"Child response using {DelegationReplayFixture.ChildModelId}",
                    },
                }),
            });

            // Wait for child message to appear
            await detail.WaitForMessageTextAsync(
                $"Child response using {DelegationReplayFixture.ChildModelId}", 5_000);

            // Check model label shows claude-haiku-4.5
            var modelLabel = Page.GetByTestId("message-model-id");
            await Assertions.Expect(modelLabel.First)
                .ToContainTextAsync(DelegationReplayFixture.ChildModelId,
                    new LocatorAssertionsToContainTextOptions { Timeout = 5_000 });
        });
    }

    // -----------------------------------------------------------------------
    // Task 12 — Parent stays busy while child works, transitions to idle
    // -----------------------------------------------------------------------

    [Fact]
    public async Task DelegationReplayE2E_Parent_BusyDuringChildThenIdle()
    {
        await WithFailureCapture(async () =>
        {
            var scenario = await SeedDelegationScenarioAsync();
            var detail = new SessionDetailPage(Page);
            await detail.GotoAsync(scenario.ParentSessionId, scenario.ParentInstanceId);

            await scenario.ParentHarness.PushEventAsync(new HarnessEvent
            {
                Type = EventTypes.SessionStatus,
                SessionId = scenario.ParentHarnessSessionId,
                FleetSessionId = scenario.ParentSessionId,
                Timestamp = DateTimeOffset.UtcNow,
                Payload = JsonSerializer.SerializeToElement(new
                {
                    sessionID = scenario.ParentHarnessSessionId,
                    status = new { type = "busy" },
                }),
            });

            await detail.WaitForBusyAsync(5_000);

            // Push parent session.status idle after child completes
            await scenario.ParentHarness.PushEventAsync(new HarnessEvent
            {
                Type = EventTypes.SessionStatus,
                SessionId = scenario.ParentHarnessSessionId,
                FleetSessionId = scenario.ParentSessionId,
                Timestamp = DateTimeOffset.UtcNow,
                Payload = JsonSerializer.SerializeToElement(new
                {
                    sessionID = scenario.ParentHarnessSessionId,
                    status = new { type = "idle" },
                }),
            });

            await detail.WaitForIdleAsync(5_000);
        });
    }

    // -----------------------------------------------------------------------
    // Task 13 — Child events delivered via WebSocket, no extra HTTP polling
    // -----------------------------------------------------------------------

    [Fact]
    public async Task DelegationReplayE2E_ChildActivity_StreamsViaWebSocketOnly()
    {
        await WithFailureCapture(async () =>
        {
            var scenario = await SeedDelegationScenarioAsync();
            var detail = new SessionDetailPage(Page);

            // Intercept any extra REST polling for child messages
            var extraChildRequestCount = 0;
            var childMessagesApiPattern = $"/api/sessions/{scenario.ChildSessionId}/messages";

            // Register response waiter BEFORE navigation so we don't miss the initial load
            var initialResponse = Page.WaitForResponseAsync(
                r => r.Url.Contains(childMessagesApiPattern, StringComparison.Ordinal) && r.Ok,
                new PageWaitForResponseOptions { Timeout = 10_000 });

            // Navigate to child session
            await detail.GotoAsync(scenario.ChildSessionId, scenario.ChildInstanceId);

            // Wait for initial load request to complete
            await initialResponse;
            await Task.Delay(300); // Let any in-flight requests settle

            // Now start counting additional requests
            Page.Request += (_, request) =>
            {
                if (request.Url.Contains(childMessagesApiPattern, StringComparison.Ordinal))
                    Interlocked.Increment(ref extraChildRequestCount);
            };
            var baseline = Volatile.Read(ref extraChildRequestCount);

            // Push child message via TestHarness (should arrive via WebSocket, not REST)
            await scenario.ChildHarness.PushEventAsync(new HarnessEvent
            {
                Type = EventTypes.MessageUpdated,
                SessionId = scenario.ChildHarnessSessionId,
                FleetSessionId = scenario.ChildSessionId,
                Timestamp = DateTimeOffset.UtcNow,
                Payload = JsonSerializer.SerializeToElement(new
                {
                    info = new
                    {
                        id = "child-msg-ws-1",
                        sessionID = scenario.ChildHarnessSessionId,
                        role = "assistant",
                        agent = DelegationReplayFixture.ChildAgent,
                        modelID = DelegationReplayFixture.ChildModelId,
                        time = new { created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
                    },
                }),
            });
            await scenario.ChildHarness.PushEventAsync(new HarnessEvent
            {
                Type = EventTypes.MessagePartUpdated,
                SessionId = scenario.ChildHarnessSessionId,
                FleetSessionId = scenario.ChildSessionId,
                Timestamp = DateTimeOffset.UtcNow,
                Payload = JsonSerializer.SerializeToElement(new
                {
                    part = new
                    {
                        id = "child-part-ws-1",
                        messageID = "child-msg-ws-1",
                        sessionID = scenario.ChildHarnessSessionId,
                        type = "text",
                        text = "Live child WebSocket-only delivery",
                    },
                }),
            });

            await detail.WaitForMessageTextAsync("Live child WebSocket-only delivery", 10_000);

            // Verify no additional HTTP polling occurred
            Volatile.Read(ref extraChildRequestCount).ShouldBe(baseline,
                "Child events must be delivered via WebSocket only, not via additional HTTP polling");
        });
    }

    // -----------------------------------------------------------------------
    // Task 14 — Parent shows busy when only child is busy (derived from child)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task DelegationReplayE2E_Parent_ShowsBusyWhenChildBusy_DerivedFromChildActivity()
    {
        await WithFailureCapture(async () =>
        {
            var scenario = await SeedDelegationScenarioAsync();
            var detail = new SessionDetailPage(Page);
            await detail.GotoAsync(scenario.ParentSessionId, scenario.ParentInstanceId);

            // Parent has NOT emitted a busy event — it is idle by itself.
            // Child emits session.status busy → EphemeralEventRelayService should propagate
            // the derived busy status to the parent's session topic.
            await scenario.ChildHarness.PushEventAsync(new HarnessEvent
            {
                Type = EventTypes.SessionStatus,
                SessionId = scenario.ChildHarnessSessionId,
                FleetSessionId = scenario.ChildSessionId,
                Timestamp = DateTimeOffset.UtcNow,
                Payload = JsonSerializer.SerializeToElement(new
                {
                    sessionID = scenario.ChildHarnessSessionId,
                    status = new { type = "busy" },
                }),
            });

            // Parent detail view should show Working, driven by child activity propagation
            await detail.WaitForBusyAsync(8_000);

            // Child goes idle → parent reverts to idle
            await scenario.ChildHarness.PushEventAsync(new HarnessEvent
            {
                Type = EventTypes.SessionStatus,
                SessionId = scenario.ChildHarnessSessionId,
                FleetSessionId = scenario.ChildSessionId,
                Timestamp = DateTimeOffset.UtcNow,
                Payload = JsonSerializer.SerializeToElement(new
                {
                    sessionID = scenario.ChildHarnessSessionId,
                    status = new { type = "idle" },
                }),
            });

            await detail.WaitForIdleAsync(8_000);
        });
    }

    private static string GetRequiredQueryValue(Uri uri, string key)
    {
        var value = System.Web.HttpUtility.ParseQueryString(uri.Query)[key];
        value.ShouldNotBeNullOrWhiteSpace();
        return value;
    }
}

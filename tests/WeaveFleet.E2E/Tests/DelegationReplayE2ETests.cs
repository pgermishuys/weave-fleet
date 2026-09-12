using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
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
/// Delegation E2E test using the shared <see cref="DelegationReplayFixture"/>: a delegated
/// child session streams its activity to the browser.
/// </summary>
[Trait("Category", "E2E")]
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

    /// <summary>
    /// Child session activity reaches the browser live over SignalR, without the client
    /// falling back to polling the messages endpoint.
    /// </summary>
    [Fact]
    public async Task DelegatedChild_StreamsLiveActivityWithoutPolling()
    {
        await WithFailureCapture(async () =>
        {
            var scenario = await SeedDelegationScenarioAsync();
            var detail = new SessionDetailPage(Page);

            // The session snapshot arrives over SignalR, so the messages endpoint
            // should not be called at all while the child is open.
            var childMessageRequests = 0;
            var childMessagesApiPattern = $"/api/sessions/{scenario.ChildSessionId}/messages";
            Page.Request += (_, request) =>
            {
                if (request.Url.Contains(childMessagesApiPattern, StringComparison.Ordinal))
                    Interlocked.Increment(ref childMessageRequests);
            };

            await detail.GotoAsync(scenario.ChildSessionId, scenario.ChildInstanceId);

            // Push a child message through the harness; it should arrive over SignalR.
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
                        text = "Live child output",
                    },
                }),
            });

            await detail.WaitForMessageTextAsync("Live child output", 10_000);

            Volatile.Read(ref childMessageRequests).ShouldBe(0,
                "Child events must arrive over SignalR, not by polling the messages endpoint");
        });
    }

    private static string GetRequiredQueryValue(Uri uri, string key)
    {
        var value = System.Web.HttpUtility.ParseQueryString(uri.Query)[key];
        value.ShouldNotBeNullOrWhiteSpace();
        return value;
    }
}

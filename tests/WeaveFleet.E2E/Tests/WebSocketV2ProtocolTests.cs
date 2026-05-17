using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Shouldly;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.E2E.Infrastructure;
using WeaveFleet.E2E.Pages;
using WeaveFleet.Infrastructure.Data.Repositories;
using WeaveFleet.TestHarness;

namespace WeaveFleet.E2E.Tests;

[Trait("Category", "E2E")]
[Trait("Lane", "WebSocket")]
public sealed class WebSocketV2ProtocolTests : E2ETestBase,
    IClassFixture<FleetWebApplicationFactory>,
    IClassFixture<PlaywrightFixture>
{
    private readonly FleetWebApplicationFactory _factory;

    public WebSocketV2ProtocolTests(FleetWebApplicationFactory factory, PlaywrightFixture playwright)
        : base(factory, playwright)
    {
        _factory = factory;
    }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        Page.SetDefaultTimeout(10_000);
        Page.SetDefaultNavigationTimeout(10_000);
        await Page.Context.AddInitScriptAsync("""
            window.localStorage.setItem('weave_v2_stream', 'true');
            """);
    }

    [Fact]
    public async Task V2_SendPrompt_ReceivesTextResponse_SessionTransitionsToIdle()
    {
        await WithFailureCapture(async () =>
        {
            const string responseText = "This is the streamed response from TestHarness";

            ConfigureScenario(builder =>
                builder.WithSimpleTextResponse(
                    "_placeholder_",
                    "msg-v2-stream-1",
                    responseText,
                    TimeSpan.FromMilliseconds(500)));

            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();

            var dialog = await dashboard.ClickNewSessionAsync();
            await dialog.SetDirectoryAsync(GetWorkspaceDirectory());

            var detail = await dialog.SubmitAsync();
            await detail.WaitForLoadedAsync();

            var sessionId = GetSessionIdFromUrl();
            await WaitForV2SnapshotAsync(sessionId);

            var initialStatus = await detail.GetStatusAsync();
            initialStatus.ShouldBe("idle");

            await detail.SendPromptAsync("Tell me something");

            await detail.WaitForBusyAsync(30_000);
            await detail.WaitForMessageTextAsync("Tell me something", 30_000);
            await detail.WaitForMessageTextAsync(responseText, 30_000);
            await detail.WaitForMessageCountAsync(2, 30_000);
            await detail.WaitForIdleAsync(30_000);
        });
    }

    [Fact]
    public async Task V2_DirectLoad_RendersPersistedMessagesFromSnapshot()
    {
        await WithFailureCapture(async () =>
        {
            var seeded = await SeedPersistedSessionAsync(
                "DB-first render test",
                [
                    MakeMessage("msg-v2-db-user-1", role: "user", text: "Load from DB after reload", agentName: null, createdAtOffsetSeconds: 0),
                    MakeMessage("msg-v2-db-assistant-1", role: "assistant", text: "Persisted response rendered from the snapshot", agentName: "loom", createdAtOffsetSeconds: 1),
                ]);

            var detail = new SessionDetailPage(Page);
            await detail.GotoAsync(seeded.SessionId, seeded.InstanceId);
            await WaitForV2SnapshotAsync(seeded.SessionId);

            await detail.WaitForMessageTextAsync("Load from DB after reload", 30_000);
            await detail.WaitForMessageTextAsync("Persisted response rendered from the snapshot", 30_000);
            await detail.WaitForMessageCountAsync(2, 30_000);
        });
    }

    [Fact]
    public async Task V2_PageRefresh_RendersConsistentState()
    {
        await WithFailureCapture(async () =>
        {
            const string promptText = "Please persist this exchange";
            const string responseText = "Snapshot should replay this response after refresh";

            ConfigureScenario(builder =>
                builder.WithSimpleTextResponse(
                    "_placeholder_",
                    "msg-v2-refresh-1",
                    responseText,
                    TimeSpan.FromMilliseconds(500)));

            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();

            var dialog = await dashboard.ClickNewSessionAsync();
            await dialog.SetDirectoryAsync(GetWorkspaceDirectory());

            var detail = await dialog.SubmitAsync();
            await detail.WaitForLoadedAsync();

            var sessionId = GetSessionIdFromUrl();
            await WaitForV2SnapshotAsync(sessionId);

            await detail.SendPromptAsync(promptText);
            await detail.WaitForMessageTextAsync(promptText, 30_000);
            await detail.WaitForMessageTextAsync(responseText, 30_000);
            await detail.WaitForIdleAsync(30_000);

            await Page.ReloadAsync();
            await detail.WaitForLoadedAsync();
            await WaitForV2SnapshotAsync(sessionId);

            await detail.WaitForMessageTextAsync(promptText, 30_000);
            await detail.WaitForMessageTextAsync(responseText, 30_000);
            await detail.WaitForMessageCountAsync(2, 30_000);
        });
    }

    [Fact]
    public async Task V2_NavigateAway_AndBack_ShowsAccumulatedMessages()
    {
        await WithFailureCapture(async () =>
        {
            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();

            var dialog = await dashboard.ClickNewSessionAsync();
            await dialog.SetDirectoryAsync(GetWorkspaceDirectory());

            var detail = await dialog.SubmitAsync();
            await detail.WaitForLoadedAsync();

            var sessionUri = new Uri(Page.Url);
            var sessionId = sessionUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();
            var instanceId = GetRequiredQueryValue(sessionUri, "instanceId");
            await WaitForV2SnapshotAsync(sessionId);

            var harness = GetHarness(instanceId);
            var harnessSessionId = harness.InstanceId;

            await PushDurableAssistantMessageAsync(
                harness,
                harnessSessionId,
                sessionId,
                "msg-v2-away-online-1",
                "Initial v2 websocket message");

            await detail.WaitForMessageTextAsync("Initial v2 websocket message", 30_000);

            await Page.GoBackAsync();
            await dashboard.WaitForLoadedAsync();
            await dashboard.GetSessionCard(sessionId).WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 10_000,
            });

            await PushDurableAssistantMessageAsync(
                harness,
                harnessSessionId,
                sessionId,
                "msg-v2-away-online-2",
                "Accumulated while away via snapshot");

            detail = await dashboard.ClickSessionCardAsync(sessionId);
            await WaitForV2MessageInSnapshotAsync(sessionId, "Accumulated while away via snapshot");

            await detail.WaitForMessageTextAsync("Initial v2 websocket message", 30_000);
            await detail.WaitForMessageTextAsync("Accumulated while away via snapshot", 30_000);
            await detail.WaitForMessageCountAsync(2, 30_000);
        });
    }

    [Fact]
    public async Task V2_LiveDomainEvents_AppearInRealTime()
    {
        await WithFailureCapture(async () =>
        {
            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();

            var dialog = await dashboard.ClickNewSessionAsync();
            await dialog.SetDirectoryAsync(GetWorkspaceDirectory());

            var detail = await dialog.SubmitAsync();
            await detail.WaitForLoadedAsync();

            var sessionUri = new Uri(Page.Url);
            var sessionId = sessionUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();
            var instanceId = GetRequiredQueryValue(sessionUri, "instanceId");
            await WaitForV2SnapshotAsync(sessionId);

            var harness = GetHarness(instanceId);
            var harnessSessionId = harness.InstanceId;

            await PushDurableAssistantMessageAsync(
                harness,
                harnessSessionId,
                sessionId,
                "msg-v2-live-1",
                "Live v2 domain event text");

            await detail.WaitForMessageTextAsync("Live v2 domain event text", 30_000);
            await detail.WaitForMessageCountAsync(1, 30_000);
        });
    }

    private Task<IJSHandle> WaitForV2SnapshotAsync(string sessionId)
        => Page.WaitForFunctionAsync(
            "topic => window.__WEAVE_SOCKET_TEST_API?.hasV2Snapshot?.(topic) === true",
            $"session:{sessionId}",
            new PageWaitForFunctionOptions
            {
                Timeout = 30_000,
            });

    private Task<IJSHandle> WaitForV2MessageInSnapshotAsync(string sessionId, string text)
        => Page.WaitForFunctionAsync(
            "([topic, expectedText]) => window.__WEAVE_SOCKET_TEST_API?.v2SnapshotHasText?.(topic, expectedText) === true",
            new[] { $"session:{sessionId}", text },
            new PageWaitForFunctionOptions
            {
                Timeout = 30_000,
            });

    private string GetSessionIdFromUrl()
    {
        var sessionUri = new Uri(Page.Url);
        return sessionUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();
    }

    private TestHarnessSession GetHarness(string instanceId)
    {
        var tracker = _factory.KestrelServices.GetRequiredService<InstanceTracker>();
        return tracker.Get(instanceId).ShouldBeOfType<TestHarnessSession>();
    }

    private async Task<SeededSession> SeedPersistedSessionAsync(string title, IReadOnlyList<SeededMessageInput> messages)
    {
        var now = DateTimeOffset.UtcNow;
        var workspaceId = $"ws-v2-{Guid.NewGuid():N}";
        var instanceId = $"inst-v2-{Guid.NewGuid():N}";
        var sessionId = $"sess-v2-{Guid.NewGuid():N}";

        var connFactory = _factory.KestrelServices.GetRequiredService<IDbConnectionFactory>();
        var userContext = new TestUserContext("local-user");

        var workspaceRepo = new WorkspaceRepository(connFactory, userContext);
        await workspaceRepo.InsertAsync(new Workspace
        {
            Id = workspaceId,
            Directory = GetWorkspaceDirectory(),
            IsolationStrategy = "existing",
            CreatedAt = now.ToString("O"),
            UserId = userContext.UserId,
        });

        var instanceRepo = new InstanceRepository(connFactory, userContext);
        await instanceRepo.InsertAsync(new Instance
        {
            Id = instanceId,
            Port = 0,
            Directory = GetWorkspaceDirectory(),
            Url = "http://127.0.0.1:0",
            Status = "stopped",
            CreatedAt = now.ToString("O"),
            UserId = userContext.UserId,
        });

        var sessionRepo = new SessionRepository(connFactory, userContext);
        await sessionRepo.InsertAsync(new Session
        {
            Id = sessionId,
            WorkspaceId = workspaceId,
            InstanceId = instanceId,
            OpencodeSessionId = $"oc-v2-{Guid.NewGuid():N}",
            Title = title,
            Status = "stopped",
            Directory = GetWorkspaceDirectory(),
            CreatedAt = now.ToString("O"),
            StoppedAt = now.ToString("O"),
            HarnessType = "opencode",
            UserId = userContext.UserId,
        });

        var messageRepo = new MessageRepository(connFactory, userContext);
        await messageRepo.UpsertBatchAsync(messages
            .Select(message => ToPersistedMessage(message, sessionId, now))
            .ToList());

        return new SeededSession(sessionId, instanceId);
    }

    private static PersistedMessage ToPersistedMessage(SeededMessageInput input, string sessionId, DateTimeOffset now)
    {
        var timestamp = now.AddSeconds(input.CreatedAtOffsetSeconds);
        return new PersistedMessage
        {
            Id = input.Id,
            SessionId = sessionId,
            Role = input.Role,
            PartsJson = $$"""[{"type":"text","kind":0,"text":"{{input.Text}}"}]""",
            Timestamp = timestamp.ToString("O"),
            CreatedAt = timestamp.ToString("O"),
            AgentName = input.AgentName,
        };
    }

    private static SeededMessageInput MakeMessage(string id, string role, string text, string? agentName, int createdAtOffsetSeconds)
        => new(id, role, text, agentName, createdAtOffsetSeconds);

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
                sessionID = harnessSessionId,
                part = new
                {
                    type = "text",
                    id = $"part-{messageId}",
                    messageID = messageId,
                    sessionID = harnessSessionId,
                    text,
                }
            })
        });
    }

    private static string GetRequiredQueryValue(Uri uri, string key)
    {
        var query = uri.Query.TrimStart('?');
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && string.Equals(Uri.UnescapeDataString(parts[0]), key, StringComparison.Ordinal))
                return Uri.UnescapeDataString(parts[1]);
        }

        throw new InvalidOperationException($"Missing required query parameter '{key}'.");
    }

    private static string GetWorkspaceDirectory()
        => Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);

    private sealed record SeededSession(string SessionId, string InstanceId);

    private sealed record SeededMessageInput(
        string Id,
        string Role,
        string Text,
        string? AgentName,
        int CreatedAtOffsetSeconds);
}

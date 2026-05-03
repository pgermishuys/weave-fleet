using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.Data;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.E2E.Infrastructure;
using WeaveFleet.E2E.Pages;
using WeaveFleet.Infrastructure.Data.Repositories;
using WeaveFleet.TestHarness;

namespace WeaveFleet.E2E.Tests;

/// <summary>
/// E2E tests verifying that persisted messages retain agent identity across page reloads.
/// Seeds messages directly into the database and asserts the UI displays the correct sender names
/// (e.g. "Loom", "Thread") instead of the generic "Assistant" fallback.
/// </summary>
[Trait("Category", "E2E")]
[Trait("Lane", "Workflow")]
public sealed class MessagePersistenceTests : E2ETestBase,
    IClassFixture<FleetWebApplicationFactory>,
    IClassFixture<PlaywrightFixture>
{
    private readonly FleetWebApplicationFactory _factory;

    public MessagePersistenceTests(FleetWebApplicationFactory factory, PlaywrightFixture playwright)
        : base(factory, playwright)
    {
        _factory = factory;
    }

    /// <summary>
    /// Verifies that agent names persist across page loads.
    /// Seeds messages with different agent names into the database,
    /// navigates to the session, and asserts each message displays
    /// the correct sender name.
    /// </summary>
    [Fact]
    public async Task PersistedMessages_DisplayCorrectAgentNames()
    {
        await WithFailureCapture(async () =>
        {
            // ── Arrange: seed workspace, instance, session, and messages ──
            var now = DateTimeOffset.UtcNow;
            var workspaceId = $"ws-{Guid.NewGuid():N}";
            var instanceId = $"inst-{Guid.NewGuid():N}";
            var sessionId = $"sess-{Guid.NewGuid():N}";

            var connFactory = _factory.KestrelServices.GetRequiredService<IDbConnectionFactory>();
            var userContext = new TestUserContext("local-user");

            var workspaceRepo = new DapperWorkspaceRepository(connFactory, userContext);
            await workspaceRepo.InsertAsync(new Workspace
            {
                Id = workspaceId,
                Directory = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
                IsolationStrategy = "existing",
                CreatedAt = now.ToString("O"),
                UserId = userContext.UserId,
            });

            var instanceRepo = new DapperInstanceRepository(connFactory, userContext);
            await instanceRepo.InsertAsync(new Instance
            {
                Id = instanceId,
                Port = 0,
                Directory = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
                Url = "http://127.0.0.1:0",
                Status = "stopped",
                CreatedAt = now.ToString("O"),
                UserId = userContext.UserId,
            });

            var sessionRepo = new DapperSessionRepository(connFactory, userContext);
            await sessionRepo.InsertAsync(new Session
            {
                Id = sessionId,
                WorkspaceId = workspaceId,
                InstanceId = instanceId,
                OpencodeSessionId = $"oc-{Guid.NewGuid():N}",
                Title = "Agent Name Persistence Test",
                Status = "stopped",
                Directory = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
                CreatedAt = now.ToString("O"),
                StoppedAt = now.ToString("O"),
                HarnessType = "opencode",
                UserId = userContext.UserId,
            });

            var messageRepo = new DapperMessageRepository(connFactory, userContext);
            await messageRepo.UpsertBatchAsync(
            [
                MakeMessage("msg-user-1", sessionId, "user", "Hello, can you help?", now, agentName: null),
                MakeMessage("msg-loom-1", sessionId, "assistant", "I'll coordinate the work.", now.AddSeconds(1), agentName: "loom"),
                MakeMessage("msg-thread-1", sessionId, "assistant", "Found 3 relevant files.", now.AddSeconds(2), agentName: "thread"),
                MakeMessage("msg-noagent-1", sessionId, "assistant", "Generic response text.", now.AddSeconds(3), agentName: null),
            ]);

            // ── Act: navigate to the session detail page ──
            var detail = new SessionDetailPage(Page);
            await detail.GotoAsync(sessionId, instanceId);

            // Wait for all 4 messages to render
            await detail.WaitForMessageCountAsync(4);

            // ── Assert: verify sender names ──
            var userNames = await detail.GetSenderNamesByRoleAsync("user");
            userNames.Count.ShouldBe(1);
            userNames[0].ShouldBe("You");

            var assistantNames = await detail.GetSenderNamesByRoleAsync("assistant");
            assistantNames.Count.ShouldBe(3);
            assistantNames.ShouldContain("Loom");
            assistantNames.ShouldContain("Thread");
            assistantNames.ShouldContain("Assistant"); // fallback for null agent
        });
    }

    /// <summary>
    /// Verifies that a session with a single agent message
    /// displays the correct title-cased agent name, not "Assistant".
    /// </summary>
    [Fact]
    public async Task PersistedMessage_WithAgent_DoesNotShowGenericAssistant()
    {
        await WithFailureCapture(async () =>
        {
            var now = DateTimeOffset.UtcNow;
            var workspaceId = $"ws-{Guid.NewGuid():N}";
            var instanceId = $"inst-{Guid.NewGuid():N}";
            var sessionId = $"sess-{Guid.NewGuid():N}";

            var connFactory = _factory.KestrelServices.GetRequiredService<IDbConnectionFactory>();
            var userContext = new TestUserContext("local-user");

            var workspaceRepo = new DapperWorkspaceRepository(connFactory, userContext);
            await workspaceRepo.InsertAsync(new Workspace
            {
                Id = workspaceId,
                Directory = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
                IsolationStrategy = "existing",
                CreatedAt = now.ToString("O"),
                UserId = userContext.UserId,
            });

            var instanceRepo = new DapperInstanceRepository(connFactory, userContext);
            await instanceRepo.InsertAsync(new Instance
            {
                Id = instanceId,
                Port = 0,
                Directory = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
                Url = "http://127.0.0.1:0",
                Status = "stopped",
                CreatedAt = now.ToString("O"),
                UserId = userContext.UserId,
            });

            var sessionRepo = new DapperSessionRepository(connFactory, userContext);
            await sessionRepo.InsertAsync(new Session
            {
                Id = sessionId,
                WorkspaceId = workspaceId,
                InstanceId = instanceId,
                OpencodeSessionId = $"oc-{Guid.NewGuid():N}",
                Title = "Single Agent Test",
                Status = "stopped",
                Directory = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
                CreatedAt = now.ToString("O"),
                StoppedAt = now.ToString("O"),
                HarnessType = "opencode",
                UserId = userContext.UserId,
            });

            var messageRepo = new DapperMessageRepository(connFactory, userContext);
            await messageRepo.UpsertBatchAsync(
            [
                MakeMessage("msg-u-1", sessionId, "user", "What is Weave Fleet?", now, agentName: null),
                MakeMessage("msg-shuttle-1", sessionId, "assistant", "Weave Fleet is a multi-agent orchestration platform.", now.AddSeconds(1), agentName: "shuttle"),
            ]);

            var detail = new SessionDetailPage(Page);
            await detail.GotoAsync(sessionId, instanceId);

            await detail.WaitForMessageCountAsync(2);

            // The assistant message should show "Shuttle", not "Assistant"
            var assistantMsg = detail.GetMessagesByRole("assistant").First;
            var senderName = await SessionDetailPage.GetMessageSenderNameAsync(assistantMsg);
            senderName.ShouldBe("Shuttle");

            // The user message should show "You"
            var userMsg = detail.GetMessagesByRole("user").First;
            var userName = await SessionDetailPage.GetMessageSenderNameAsync(userMsg);
            userName.ShouldBe("You");
        });
    }

    [Fact]
    public async Task DirectLoad_RendersPersistedActivityFromDatabase()
    {
        await WithFailureCapture(async () =>
        {
            var now = DateTimeOffset.UtcNow;
            var workspaceId = $"ws-db-first-{Guid.NewGuid():N}";
            var instanceId = $"inst-db-first-{Guid.NewGuid():N}";
            var sessionId = $"sess-db-first-{Guid.NewGuid():N}";

            var connFactory = _factory.KestrelServices.GetRequiredService<IDbConnectionFactory>();
            var userContext = new TestUserContext("local-user");

            var workspaceRepo = new DapperWorkspaceRepository(connFactory, userContext);
            await workspaceRepo.InsertAsync(new Workspace
            {
                Id = workspaceId,
                Directory = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
                IsolationStrategy = "existing",
                CreatedAt = now.ToString("O"),
                UserId = userContext.UserId,
            });

            var instanceRepo = new DapperInstanceRepository(connFactory, userContext);
            await instanceRepo.InsertAsync(new Instance
            {
                Id = instanceId,
                Port = 0,
                Directory = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
                Url = "http://127.0.0.1:0",
                Status = "stopped",
                CreatedAt = now.ToString("O"),
                UserId = userContext.UserId,
            });

            var sessionRepo = new DapperSessionRepository(connFactory, userContext);
            await sessionRepo.InsertAsync(new Session
            {
                Id = sessionId,
                WorkspaceId = workspaceId,
                InstanceId = instanceId,
                OpencodeSessionId = $"oc-db-first-{Guid.NewGuid():N}",
                Title = "DB-first render test",
                Status = "stopped",
                Directory = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
                CreatedAt = now.ToString("O"),
                StoppedAt = now.ToString("O"),
                HarnessType = "opencode",
                UserId = userContext.UserId,
            });

            var messageRepo = new DapperMessageRepository(connFactory, userContext);
            await messageRepo.UpsertBatchAsync(
            [
                MakeMessage("msg-db-user-1", sessionId, "user", "Load from DB after reload", now, agentName: null),
                MakeMessage("msg-db-assistant-1", sessionId, "assistant", "Persisted response rendered from the database", now.AddSeconds(1), agentName: "loom"),
            ]);

            await Page.GotoAsync($"/sessions/{Uri.EscapeDataString(sessionId)}?instanceId={Uri.EscapeDataString(instanceId)}");
            await Page.GetByTestId("activity-stream").WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 10_000,
            });
            var detail = new SessionDetailPage(Page);
            await detail.WaitForMessageTextAsync("Load from DB after reload", 10_000);
            await detail.WaitForMessageTextAsync("Persisted response rendered from the database", 10_000);
        });
    }

    [Fact]
    public async Task DirectLoad_RendersPersistedPromptAndAssistantResponseFromDatabase()
    {
        await WithFailureCapture(async () =>
        {
            var now = DateTimeOffset.UtcNow;
            var workspaceId = $"ws-db-history-{Guid.NewGuid():N}";
            var instanceId = $"inst-db-history-{Guid.NewGuid():N}";
            var sessionId = $"sess-db-history-{Guid.NewGuid():N}";

            var connFactory = _factory.KestrelServices.GetRequiredService<IDbConnectionFactory>();
            var userContext = new TestUserContext("local-user");

            var workspaceRepo = new DapperWorkspaceRepository(connFactory, userContext);
            await workspaceRepo.InsertAsync(new Workspace
            {
                Id = workspaceId,
                Directory = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
                IsolationStrategy = "existing",
                CreatedAt = now.ToString("O"),
                UserId = userContext.UserId,
            });

            var instanceRepo = new DapperInstanceRepository(connFactory, userContext);
            await instanceRepo.InsertAsync(new Instance
            {
                Id = instanceId,
                Port = 0,
                Directory = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
                Url = "http://127.0.0.1:0",
                Status = "stopped",
                CreatedAt = now.ToString("O"),
                UserId = userContext.UserId,
            });

            var sessionRepo = new DapperSessionRepository(connFactory, userContext);
            await sessionRepo.InsertAsync(new Session
            {
                Id = sessionId,
                WorkspaceId = workspaceId,
                InstanceId = instanceId,
                OpencodeSessionId = $"oc-db-history-{Guid.NewGuid():N}",
                Title = "DB history fidelity test",
                Status = "stopped",
                Directory = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
                CreatedAt = now.ToString("O"),
                StoppedAt = now.ToString("O"),
                HarnessType = "opencode",
                UserId = userContext.UserId,
            });

            var messageRepo = new DapperMessageRepository(connFactory, userContext);
            await messageRepo.UpsertBatchAsync(
            [
                MakeMessage("msg-db-prompt-1", sessionId, "user", "Persisted prompt text survives reload", now, agentName: null),
                MakeMessage("msg-db-answer-1", sessionId, "assistant", "Persisted assistant text survives reload", now.AddSeconds(1), agentName: "loom"),
            ]);

            var detail = new SessionDetailPage(Page);
            await detail.GotoAsync(sessionId, instanceId);

            await detail.WaitForMessageTextAsync("Persisted prompt text survives reload", 10_000);
            await detail.WaitForMessageTextAsync("Persisted assistant text survives reload", 10_000);
        });
    }

    [Fact]
    public async Task NavigationBackToSession_ReplaysMissedCommittedEvents_ViaSequenceGapFill()
    {
        await WithFailureCapture(async () =>
        {
            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();

            var dialog = await dashboard.ClickNewSessionAsync();
            await dialog.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));

            var detail = await dialog.SubmitAsync();
            await detail.WaitForLoadedAsync();

            var sessionUri = new Uri(Page.Url);
            var sessionId = sessionUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();
            var instanceId = GetRequiredQueryValue(sessionUri, "instanceId");

            var tracker = _factory.KestrelServices.GetRequiredService<InstanceTracker>();
            var harness = tracker.Get(instanceId).ShouldBeOfType<TestHarnessSession>();
            var harnessSessionId = harness.InstanceId;

            await PushDurableAssistantMessageAsync(
                harness,
                harnessSessionId,
                sessionId,
                "msg-online-1",
                "Initial committed websocket event");

            await detail.WaitForMessageTextAsync("Initial committed websocket event", 10_000);

            await Page.GoBackAsync();
            await dashboard.WaitForLoadedAsync();
            await dashboard.GetSessionCard(sessionId)
                .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

            await PushDurableAssistantMessageAsync(
                harness,
                harnessSessionId,
                sessionId,
                "msg-gapfill-1",
                "Recovered after reconnect via sequence gap fill");

            var afterSequenceNumberResponse = Page.WaitForResponseAsync(response =>
                response.Url.Contains($"/api/sessions/{sessionId}/committed-events", StringComparison.Ordinal)
                && response.Url.Contains("afterSequenceNumber=", StringComparison.Ordinal)
                && response.Ok,
                new PageWaitForResponseOptions { Timeout = 30_000 });

            detail = await dashboard.ClickSessionCardAsync(sessionId);

            var gapFillResponse = await afterSequenceNumberResponse;
            gapFillResponse.Url.ShouldContain("afterSequenceNumber=");
            await detail.WaitForMessageTextAsync("Recovered after reconnect via sequence gap fill", 30_000);
        });
    }

    [Fact]
    public async Task SwitchingFromAnotherSession_ReplaysMissedCommittedEvents_ViaSequenceGapFill()
    {
        await WithFailureCapture(async () =>
        {
            var dashboard = new FleetDashboardPage(Page);
            var sidebar = new FleetSidebarPage(Page);
            await dashboard.GotoAsync();

            var dialog = await dashboard.ClickNewSessionAsync();
            await dialog.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
            await dialog.SetTitleAsync("Session A");

            var sessionADetail = await dialog.SubmitAsync();
            await sessionADetail.WaitForLoadedAsync();

            var sessionAUri = new Uri(Page.Url);
            var sessionAId = sessionAUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();
            var sessionAInstanceId = GetRequiredQueryValue(sessionAUri, "instanceId");

            var tracker = _factory.KestrelServices.GetRequiredService<InstanceTracker>();
            var sessionAHarness = tracker.Get(sessionAInstanceId).ShouldBeOfType<TestHarnessSession>();
            var sessionAHarnessId = sessionAHarness.InstanceId;

            await PushDurableAssistantMessageAsync(
                sessionAHarness,
                sessionAHarnessId,
                sessionAId,
                "msg-a-online-1",
                "Initial committed message in session A");

            await sessionADetail.WaitForMessageTextAsync("Initial committed message in session A", 10_000);

            await Page.GoBackAsync();
            await dashboard.WaitForLoadedAsync();
            await dashboard.GetSessionCard(sessionAId)
                .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

            dialog = await dashboard.ClickNewSessionAsync();
            await dialog.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
            await dialog.SetTitleAsync("Session B");

            var sessionBDetail = await dialog.SubmitAsync();
            await sessionBDetail.WaitForLoadedAsync();

            await sidebar.ExpectSessionVisibleAsync(sessionAId);

            await PushDurableAssistantMessageAsync(
                sessionAHarness,
                sessionAHarnessId,
                sessionAId,
                "msg-a-gapfill-1",
                "Recovered final message after switching from session B");

            var afterSequenceNumberResponse = Page.WaitForResponseAsync(response =>
                response.Url.Contains($"/api/sessions/{sessionAId}/committed-events", StringComparison.Ordinal)
                && response.Url.Contains("afterSequenceNumber=", StringComparison.Ordinal)
                && response.Ok,
                new PageWaitForResponseOptions { Timeout = 30_000 });

            sessionADetail = await sidebar.ClickSessionAsync(sessionAId);

            var gapFillResponse = await afterSequenceNumberResponse;
            gapFillResponse.Url.ShouldContain("afterSequenceNumber=");
            await sessionADetail.WaitForMessageTextAsync("Recovered final message after switching from session B", 30_000);
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

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

    private static string GetRequiredQueryValue(Uri uri, string key)
    {
        var query = uri.Query.TrimStart('?');
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && string.Equals(Uri.UnescapeDataString(parts[0]), key, StringComparison.Ordinal))
            {
                return Uri.UnescapeDataString(parts[1]);
            }
        }

        throw new InvalidOperationException($"Missing required query parameter '{key}'.");
    }

    private static PersistedMessage MakeMessage(
        string id, string sessionId, string role, string text,
        DateTimeOffset timestamp, string? agentName)
    {
        return new PersistedMessage
        {
            Id = id,
            SessionId = sessionId,
            Role = role,
            PartsJson = $$"""[{"type":"text","kind":0,"text":"{{text}}"}]""",
            Timestamp = timestamp.ToString("O"),
            CreatedAt = timestamp.ToString("O"),
            AgentName = agentName,
        };
    }
}

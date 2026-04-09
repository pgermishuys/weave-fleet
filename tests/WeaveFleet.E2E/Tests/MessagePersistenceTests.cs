using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Data;
using WeaveFleet.Domain.Entities;
using WeaveFleet.E2E.Infrastructure;
using WeaveFleet.E2E.Pages;
using WeaveFleet.Infrastructure.Data.Repositories;

namespace WeaveFleet.E2E.Tests;

/// <summary>
/// E2E tests verifying that persisted messages retain agent identity across page reloads.
/// Seeds messages directly into the database and asserts the UI displays the correct sender names
/// (e.g. "Loom", "Thread") instead of the generic "Assistant" fallback.
/// </summary>
[Trait("Category", "E2E")]
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

            var workspaceRepo = new DapperWorkspaceRepository(connFactory);
            await workspaceRepo.InsertAsync(new Workspace
            {
                Id = workspaceId,
                Directory = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
                IsolationStrategy = "existing",
                CreatedAt = now.ToString("O"),
            });

            var instanceRepo = new DapperInstanceRepository(connFactory);
            await instanceRepo.InsertAsync(new Instance
            {
                Id = instanceId,
                Port = 0,
                Directory = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
                Url = "http://127.0.0.1:0",
                Status = "stopped",
                CreatedAt = now.ToString("O"),
            });

            var sessionRepo = new DapperSessionRepository(connFactory);
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
            });

            var messageRepo = new DapperMessageRepository(connFactory);
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

            var workspaceRepo = new DapperWorkspaceRepository(connFactory);
            await workspaceRepo.InsertAsync(new Workspace
            {
                Id = workspaceId,
                Directory = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
                IsolationStrategy = "existing",
                CreatedAt = now.ToString("O"),
            });

            var instanceRepo = new DapperInstanceRepository(connFactory);
            await instanceRepo.InsertAsync(new Instance
            {
                Id = instanceId,
                Port = 0,
                Directory = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
                Url = "http://127.0.0.1:0",
                Status = "stopped",
                CreatedAt = now.ToString("O"),
            });

            var sessionRepo = new DapperSessionRepository(connFactory);
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
            });

            var messageRepo = new DapperMessageRepository(connFactory);
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

    // ── Helpers ──────────────────────────────────────────────────────────────

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

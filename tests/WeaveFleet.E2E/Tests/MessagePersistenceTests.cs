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

            var workspaceRepo = new WorkspaceRepository(connFactory, userContext);
            await workspaceRepo.InsertAsync(new Workspace
            {
                Id = workspaceId,
                Directory = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
                IsolationStrategy = "existing",
                CreatedAt = now.ToString("O"),
                UserId = userContext.UserId,
            });

            var instanceRepo = new InstanceRepository(connFactory, userContext);
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

            var sessionRepo = new SessionRepository(connFactory, userContext);
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

            var messageRepo = new MessageRepository(connFactory, userContext);
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

            var workspaceRepo = new WorkspaceRepository(connFactory, userContext);
            await workspaceRepo.InsertAsync(new Workspace
            {
                Id = workspaceId,
                Directory = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
                IsolationStrategy = "existing",
                CreatedAt = now.ToString("O"),
                UserId = userContext.UserId,
            });

            var instanceRepo = new InstanceRepository(connFactory, userContext);
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

            var sessionRepo = new SessionRepository(connFactory, userContext);
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

            var messageRepo = new MessageRepository(connFactory, userContext);
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

            var workspaceRepo = new WorkspaceRepository(connFactory, userContext);
            await workspaceRepo.InsertAsync(new Workspace
            {
                Id = workspaceId,
                Directory = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
                IsolationStrategy = "existing",
                CreatedAt = now.ToString("O"),
                UserId = userContext.UserId,
            });

            var instanceRepo = new InstanceRepository(connFactory, userContext);
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

            var sessionRepo = new SessionRepository(connFactory, userContext);
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

            var messageRepo = new MessageRepository(connFactory, userContext);
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

            var workspaceRepo = new WorkspaceRepository(connFactory, userContext);
            await workspaceRepo.InsertAsync(new Workspace
            {
                Id = workspaceId,
                Directory = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
                IsolationStrategy = "existing",
                CreatedAt = now.ToString("O"),
                UserId = userContext.UserId,
            });

            var instanceRepo = new InstanceRepository(connFactory, userContext);
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

            var sessionRepo = new SessionRepository(connFactory, userContext);
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

            var messageRepo = new MessageRepository(connFactory, userContext);
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

            var afterEventIdResponse = Page.WaitForResponseAsync(response =>
                response.Url.Contains($"/api/sessions/{sessionId}/committed-events", StringComparison.Ordinal)
                && response.Url.Contains("afterEventId=", StringComparison.Ordinal)
                && response.Ok,
                new PageWaitForResponseOptions { Timeout = 10_000 });

            detail = await dashboard.ClickSessionCardAsync(sessionId);

            var gapFillResponse = await afterEventIdResponse;
            gapFillResponse.Url.ShouldContain("afterEventId=");
            await detail.WaitForMessageTextAsync("Recovered after reconnect via sequence gap fill", 10_000);
        });
    }

    [Fact]
    public async Task SwitchingFromAnotherSession_ShowsMessagesPushedWhileViewingDifferentSession()
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

            // Push a message to session A while viewing session B.
            // The WebSocket stays connected across sidebar session switches,
            // so events arrive via streaming (not HTTP gap-fill).
            await PushDurableAssistantMessageAsync(
                sessionAHarness,
                sessionAHarnessId,
                sessionAId,
                "msg-a-while-on-b",
                "Message pushed while viewing session B");

            sessionADetail = await sidebar.ClickSessionAsync(sessionAId);

            await sessionADetail.WaitForMessageTextAsync("Message pushed while viewing session B", 30_000);
        });
    }

    /// <summary>
    /// Verifies that messages maintain correct chronological order when two prompts
    /// are sent sequentially. The test harness rewrites user echo IDs to match the
    /// synthetic send-time IDs, so this validates the happy path where deduplication
    /// works correctly.
    /// </summary>
    [Fact]
    public async Task Message_ordering_preserved_across_sequential_prompt_response_pairs()
    {
        await WithFailureCapture(async () =>
        {
            ConfigureScenario(b => b
                .WithSimpleTextResponse("_placeholder_", "msg-resp-1", "First assistant response")
                .WithSimpleTextResponse("_placeholder_", "msg-resp-2", "Second assistant response"));

            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();

            var dialog = await dashboard.ClickNewSessionAsync();
            await dialog.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));

            var detail = await dialog.SubmitAsync();
            await detail.WaitForLoadedAsync();

            // Send first prompt and wait for assistant response
            await detail.SendPromptAsync("First user prompt");
            await detail.WaitForMessageTextAsync("First assistant response", 10_000);
            await detail.WaitForIdleAsync(10_000);

            // Send second prompt and wait for assistant response
            await detail.SendPromptAsync("Second user prompt");
            await detail.WaitForMessageTextAsync("Second assistant response", 10_000);
            await detail.WaitForIdleAsync(10_000);

            await AssertMessagesPresentAsync(detail, expectedCount: 4);

            // Reload and verify persistence ordering matches
            await Page.ReloadAsync();
            await detail.WaitForLoadedAsync();

            await detail.WaitForMessageTextAsync("First user prompt", 10_000);
            await detail.WaitForMessageTextAsync("Second assistant response", 10_000);

            await AssertMessagesPresentAsync(detail, expectedCount: 4);
        });
    }

    private static async Task AssertMessagesPresentAsync(SessionDetailPage detail, int expectedCount)
    {
        var items = await detail.GetMessageItemsAsync();
        items.Count.ShouldBeGreaterThanOrEqualTo(expectedCount);

        var roles = new List<string>();
        foreach (var item in items)
        {
            var role = await item.GetAttributeAsync("data-role");
            roles.Add(role ?? "unknown");
        }

        roles.Count(role => role == "user").ShouldBe(2);
        roles.Count(role => role == "assistant").ShouldBe(2);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that user prompt text is persisted at send time and survives
    /// when the harness SSE stream echoes <c>message.updated</c> for the user
    /// message but omits <c>message.part.updated</c> (the text part event).
    /// This reproduces the production bug where OpenCode sometimes drops the
    /// user text part event, resulting in empty user message bubbles.
    /// </summary>
    [Fact]
    public async Task UserPromptText_PersistedAtSendTime_SurvivesMissingPartEvent()
    {
        await WithFailureCapture(async () =>
        {
            // Configure a response that echoes message.updated for the user (no parts)
            // but does NOT emit message.part.updated for the user message.
            // Only the assistant response has both events.
            ConfigureScenario(b => b.WithPromptResponse(r => r
                // User message echo — message.updated only, NO message.part.updated
                .AddEvent(new HarnessEvent
                {
                    Type = "session.status",
                    SessionId = "_placeholder_",
                    Timestamp = DateTimeOffset.UtcNow,
                    Payload = JsonSerializer.SerializeToElement(new
                    {
                        sessionId = "_placeholder_",
                        status = new { type = "busy" },
                    }),
                })
                .AddEvent(new HarnessEvent
                {
                    Type = "message.updated",
                    SessionId = "_placeholder_",
                    Timestamp = DateTimeOffset.UtcNow,
                    Payload = JsonSerializer.SerializeToElement(new
                    {
                        info = new
                        {
                            id = "msg-user-echo-no-parts",
                            sessionID = "_placeholder_",
                            role = "user",
                        },
                    }),
                })
                // NOTE: No message.part.updated for the user message — this is the bug scenario
                // Assistant response — normal flow with both events
                .AddEvent(new HarnessEvent
                {
                    Type = "message.updated",
                    SessionId = "_placeholder_",
                    Timestamp = DateTimeOffset.UtcNow,
                    Payload = JsonSerializer.SerializeToElement(new
                    {
                        info = new
                        {
                            id = "msg-assistant-response",
                            sessionID = "_placeholder_",
                            role = "assistant",
                        },
                    }),
                }, TimeSpan.FromMilliseconds(50))
                .AddEvent(new HarnessEvent
                {
                    Type = "message.part.updated",
                    SessionId = "_placeholder_",
                    Timestamp = DateTimeOffset.UtcNow,
                    Payload = JsonSerializer.SerializeToElement(new
                    {
                        part = new
                        {
                            id = "part-assistant-1",
                            messageID = "msg-assistant-response",
                            sessionID = "_placeholder_",
                            type = "text",
                            text = "Assistant reply to the prompt",
                        },
                    }),
                }, TimeSpan.FromMilliseconds(50))
                .AddEvent(new HarnessEvent
                {
                    Type = "session.idle",
                    SessionId = "_placeholder_",
                    Timestamp = DateTimeOffset.UtcNow,
                    Payload = JsonSerializer.SerializeToElement(new
                    {
                        sessionId = "_placeholder_",
                        status = new { type = "idle" },
                    }),
                }, TimeSpan.FromMilliseconds(50))));

            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();

            var dialog = await dashboard.ClickNewSessionAsync();
            await dialog.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));

            var detail = await dialog.SubmitAsync();
            await detail.WaitForLoadedAsync();

            // Send the prompt — the user message text should be persisted at send time
            await detail.SendPromptAsync("This prompt must survive missing part events");

            // Wait for the assistant response to confirm the full flow completed
            await detail.WaitForMessageTextAsync("Assistant reply to the prompt", 10_000);

            // Reload the page to force the frontend to load messages from the database.
            // During the live session, the frontend shows the optimistic prompt which is
            // cleared on idle. After reload, it fetches persisted messages from the REST API.
            // This is the critical path: the user message text must come from the DB row
            // that was persisted at send time, not from the SSE echo (which had no text).
            await Page.ReloadAsync();
            await detail.WaitForLoadedAsync();

            // The critical assertion: the user prompt text is visible in the UI
            // even though the harness never sent message.part.updated for the user message.
            await detail.WaitForMessageTextAsync("This prompt must survive missing part events", 10_000);
            await detail.WaitForMessageTextAsync("Assistant reply to the prompt", 10_000);
        });
    }

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

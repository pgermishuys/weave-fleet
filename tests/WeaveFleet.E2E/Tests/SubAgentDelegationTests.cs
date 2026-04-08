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

namespace WeaveFleet.E2E.Tests;

[Trait("Category", "E2E")]
public sealed class SubAgentDelegationTests : E2ETestBase,
    IClassFixture<FleetWebApplicationFactory>,
    IClassFixture<PlaywrightFixture>
{
    private readonly FleetWebApplicationFactory _factory;

    public SubAgentDelegationTests(FleetWebApplicationFactory factory, PlaywrightFixture playwright)
        : base(factory, playwright)
    {
        _factory = factory;
    }

    [Fact]
    public async Task DelegatedChildSession_StreamsLiveActivity_AndShowsBreadcrumb()
    {
        await WithFailureCapture(async () =>
        {
            ConfigureScenario(_ => { });

            var now = DateTimeOffset.UtcNow;
            var workspaceId = $"ws-{Guid.NewGuid():N}";
            var parentInstanceId = $"inst-parent-{Guid.NewGuid():N}";
            var childInstanceId = $"inst-child-{Guid.NewGuid():N}";
            var parentSessionId = $"sess-parent-{Guid.NewGuid():N}";
            var childSessionId = $"sess-child-{Guid.NewGuid():N}";
            var parentToolCallId = $"tool-{Guid.NewGuid():N}";

            var connFactory = _factory.KestrelServices.GetRequiredService<IDbConnectionFactory>();
            var tracker = _factory.KestrelServices.GetRequiredService<InstanceTracker>();

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
                Id = parentInstanceId,
                Port = 0,
                Directory = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
                Url = "http://127.0.0.1:0",
                Status = "running",
                CreatedAt = now.ToString("O"),
            });
            await instanceRepo.InsertAsync(new Instance
            {
                Id = childInstanceId,
                Port = 0,
                Directory = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
                Url = "http://127.0.0.1:0",
                Status = "running",
                CreatedAt = now.ToString("O"),
            });

            var sessionRepo = new DapperSessionRepository(connFactory);
            await sessionRepo.InsertAsync(new Session
            {
                Id = parentSessionId,
                WorkspaceId = workspaceId,
                InstanceId = parentInstanceId,
                OpencodeSessionId = $"oc-parent-{Guid.NewGuid():N}",
                Title = "Parent Session",
                Status = "active",
                Directory = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
                CreatedAt = now.ToString("O"),
                LifecycleStatus = "running",
                ActivityStatus = "busy",
                HarnessType = "opencode",
            });
            await sessionRepo.InsertAsync(new Session
            {
                Id = childSessionId,
                WorkspaceId = workspaceId,
                InstanceId = childInstanceId,
                OpencodeSessionId = $"oc-child-{Guid.NewGuid():N}",
                Title = "Thread",
                Status = "active",
                Directory = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
                CreatedAt = now.ToString("O"),
                ParentSessionId = parentSessionId,
                LifecycleStatus = "running",
                ActivityStatus = "busy",
                HarnessType = "opencode",
                IsHidden = true,
            });

            var delegationRepo = new DapperDelegationRepository(connFactory);
            await delegationRepo.InsertAsync(new Delegation
            {
                Id = $"del-{Guid.NewGuid():N}",
                ParentSessionId = parentSessionId,
                ChildSessionId = childSessionId,
                ParentToolCallId = parentToolCallId,
                Title = "thread",
                Status = "running",
                CreatedAt = now.ToString("O"),
                UpdatedAt = now.ToString("O"),
            });

            var messageRepo = new DapperMessageRepository(connFactory);
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
                                Arguments: JsonSerializer.SerializeToElement(new { subagent_type = "thread" }),
                                State: ToolUseState.Running)
                        ],
                        Timestamp = now,
                        Agent = "loom",
                    }));

            var childHarness = new TestHarnessInstance(childInstanceId, new TestScenario());
            tracker.Register(childInstanceId, childHarness);

            var detail = new SessionDetailPage(Page);
            await detail.GotoAsync(parentSessionId, parentInstanceId);

            var delegationLink = Page.Locator(
                $"a[href=\"/sessions/{Uri.EscapeDataString(childSessionId)}?instanceId={Uri.EscapeDataString(childInstanceId)}\"]");

            await Microsoft.Playwright.Assertions.Expect(delegationLink)
                .ToBeVisibleAsync();

            var childRequestCount = 0;
            Page.Request += (_, request) =>
            {
                if (request.Url.Contains($"/api/sessions/{childSessionId}/messages", StringComparison.Ordinal))
                    Interlocked.Increment(ref childRequestCount);
            };

            await delegationLink.ClickAsync();

            await Microsoft.Playwright.Assertions.Expect(Page)
                .ToHaveURLAsync(
                    new System.Text.RegularExpressions.Regex(
                        $"/sessions/{System.Text.RegularExpressions.Regex.Escape(childSessionId)}\\?instanceId={System.Text.RegularExpressions.Regex.Escape(childInstanceId)}$"),
                    new Microsoft.Playwright.PageAssertionsToHaveURLOptions { Timeout = 5_000 });
            await detail.WaitForLoadedAsync();
            var baselineRequests = Volatile.Read(ref childRequestCount);

            await childHarness.PushEventAsync(new HarnessEvent
            {
                Type = "message.updated",
                SessionId = "oc-child-live",
                FleetSessionId = childSessionId,
                Timestamp = DateTimeOffset.UtcNow,
                Payload = System.Text.Json.JsonSerializer.SerializeToElement(new
                {
                    info = new
                    {
                        id = "msg-child-live",
                        sessionID = "oc-child-live",
                        role = "assistant",
                        time = new { created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
                        agent = "thread",
                    }
                })
            });
            await childHarness.PushEventAsync(new HarnessEvent
            {
                Type = "message.part.updated",
                SessionId = "oc-child-live",
                FleetSessionId = childSessionId,
                Timestamp = DateTimeOffset.UtcNow,
                Payload = System.Text.Json.JsonSerializer.SerializeToElement(new
                {
                    part = new
                    {
                        id = "part-child-live",
                        messageID = "msg-child-live",
                        sessionID = "oc-child-live",
                        type = "text",
                        text = "Live child websocket output"
                    }
                })
            });

            await detail.WaitForMessageTextAsync("Live child websocket output", 5_000);

            Assert.Equal(baselineRequests, Volatile.Read(ref childRequestCount));
        });
    }
}

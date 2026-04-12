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
            var childHarnessSessionId = $"oc-child-{Guid.NewGuid():N}";
            var parentSessionId = $"sess-parent-{Guid.NewGuid():N}";
            var parentToolCallId = $"tool-{Guid.NewGuid():N}";

            var connFactory = _factory.KestrelServices.GetRequiredService<IDbConnectionFactory>();
            var tracker = _factory.KestrelServices.GetRequiredService<InstanceTracker>();
            var userContext = new TestUserContext();

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
                Id = parentInstanceId,
                Port = 0,
                Directory = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
                Url = "http://127.0.0.1:0",
                Status = "running",
                CreatedAt = now.ToString("O"),
                UserId = userContext.UserId,
            });
            var sessionRepo = new DapperSessionRepository(connFactory, userContext);
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
                UserId = userContext.UserId,
            });

            var messageRepo = new DapperMessageRepository(connFactory, userContext);
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

            using (var scope = _factory.KestrelServices.CreateScope())
            {
                var delegationService = scope.ServiceProvider.GetRequiredService<DelegationService>();
                var sessionOrchestrator = scope.ServiceProvider.GetRequiredService<SessionOrchestrator>();

                await delegationService.HandleDelegationDetectedAsync(parentSessionId, parentToolCallId, "thread");

                var childSessionResult = await sessionOrchestrator.EnsureDelegatedChildSessionAsync(
                    parentSessionId,
                    childHarnessSessionId,
                    "thread");
                childSessionResult.IsSuccess.ShouldBeTrue(childSessionResult.IsFailure ? childSessionResult.Error.ToString() : null);

                var childSession = childSessionResult.Value;

                var linkedDelegation = await delegationService.HandleChildLinkedAsync(
                    parentSessionId,
                    parentToolCallId,
                    childSession.Id);
                linkedDelegation.ShouldNotBeNull();

                var childHarness = tracker.Get(childSession.InstanceId).ShouldBeOfType<TestHarnessSession>();

                var detail = new SessionDetailPage(Page);
                var dashboard = new FleetDashboardPage(Page);
                await detail.GotoAsync(parentSessionId, parentInstanceId);

                await Page.GotoAsync("/");
                await dashboard.WaitForLoadedAsync();
                await Microsoft.Playwright.Assertions.Expect(Page.GetByTestId("session-card")).ToHaveCountAsync(1);
                await Microsoft.Playwright.Assertions.Expect(Page.GetByTestId("session-title")).ToContainTextAsync("Parent Session");

                await Page.GetByRole(Microsoft.Playwright.AriaRole.Button, new() { Name = "Show: Active" }).ClickAsync();
                await Page.GetByRole(Microsoft.Playwright.AriaRole.Menuitem, new() { Name = "All" }).ClickAsync();
                await Microsoft.Playwright.Assertions.Expect(Page.GetByTestId("session-card")).ToHaveCountAsync(1);
                await Microsoft.Playwright.Assertions.Expect(Page.GetByTestId("session-title")).ToContainTextAsync("Parent Session");

                await detail.GotoAsync(parentSessionId, parentInstanceId);

                var delegationLink = Page.Locator(
                    $"a[href=\"/sessions/{Uri.EscapeDataString(childSession.Id)}?instanceId={Uri.EscapeDataString(childSession.InstanceId)}&parentSessionId={Uri.EscapeDataString(parentSessionId)}\"]");

                await Microsoft.Playwright.Assertions.Expect(delegationLink)
                    .ToBeVisibleAsync(new Microsoft.Playwright.LocatorAssertionsToBeVisibleOptions { Timeout = 10_000 });

                var childRequestCount = 0;
                Page.Request += (_, request) =>
                {
                    if (request.Url.Contains($"/api/sessions/{childSession.Id}/messages", StringComparison.Ordinal))
                        Interlocked.Increment(ref childRequestCount);
                };

                var initialChildMessagesResponse = Page.WaitForResponseAsync(response =>
                    response.Url.Contains($"/api/sessions/{childSession.Id}/messages", StringComparison.Ordinal)
                    && response.Ok);

                await delegationLink.ClickAsync();

                await Microsoft.Playwright.Assertions.Expect(Page)
                    .ToHaveURLAsync(
                        new System.Text.RegularExpressions.Regex(
                            $"/sessions/{System.Text.RegularExpressions.Regex.Escape(childSession.Id)}\\?instanceId={System.Text.RegularExpressions.Regex.Escape(childSession.InstanceId)}&parentSessionId={System.Text.RegularExpressions.Regex.Escape(parentSessionId)}$"),
                        new Microsoft.Playwright.PageAssertionsToHaveURLOptions { Timeout = 5_000 });
                await initialChildMessagesResponse;
                await detail.WaitForLoadedAsync();

                var breadcrumbLink = Page.Locator(
                    $"a[href=\"/sessions/{Uri.EscapeDataString(parentSessionId)}?instanceId={Uri.EscapeDataString(parentInstanceId)}\"]")
                    .Last;
                await Microsoft.Playwright.Assertions.Expect(breadcrumbLink)
                    .ToContainTextAsync("Parent Session");

                var baselineRequests = Volatile.Read(ref childRequestCount);

                await childHarness.PushEventAsync(new HarnessEvent
                {
                    Type = "message.updated",
                    SessionId = childHarnessSessionId,
                    FleetSessionId = childSession.Id,
                    Timestamp = DateTimeOffset.UtcNow,
                    Payload = System.Text.Json.JsonSerializer.SerializeToElement(new
                    {
                        info = new
                        {
                            id = "msg-child-live",
                            sessionID = childHarnessSessionId,
                            role = "assistant",
                            time = new { created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
                            agent = "thread",
                        }
                    })
                });
                await childHarness.PushEventAsync(new HarnessEvent
                {
                    Type = "message.part.updated",
                    SessionId = childHarnessSessionId,
                    FleetSessionId = childSession.Id,
                    Timestamp = DateTimeOffset.UtcNow,
                    Payload = System.Text.Json.JsonSerializer.SerializeToElement(new
                    {
                        part = new
                        {
                            id = "part-child-live",
                            messageID = "msg-child-live",
                            sessionID = childHarnessSessionId,
                            type = "text",
                            text = "Live child websocket output"
                        }
                    })
                });

                await detail.WaitForMessageTextAsync("Live child websocket output", 5_000);

                Volatile.Read(ref childRequestCount).ShouldBe(baselineRequests);
            }
        });
    }
}


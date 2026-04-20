using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Application.Services;
using WeaveFleet.E2E.Infrastructure;
using WeaveFleet.E2E.Pages;
using WeaveFleet.TestHarness;

namespace WeaveFleet.E2E.Tests;

/// <summary>
/// E2E coverage for session-detail WebSocket interruption and recovery.
/// Validates the user-visible disconnected state plus committed-event catch-up after reconnect
/// without reloading the SPA document.
/// </summary>
[Trait("Category", "E2E")]
[Trait("Lane", "Workflow")]
public sealed class WebSocketResilienceTests : E2ETestBase,
    IClassFixture<FleetWebApplicationFactory>,
    IClassFixture<PlaywrightFixture>
{
    private readonly FleetWebApplicationFactory _factory;

    public WebSocketResilienceTests(FleetWebApplicationFactory factory, PlaywrightFixture playwright)
        : base(factory, playwright)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ActiveSession_ShowsDisconnectedState_ThenRecoversAndCatchesUpWithoutReload()
    {
        await WithFailureCapture(async () =>
        {
            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();

            var dialog = await dashboard.ClickNewSessionAsync();
            await dialog.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
            await dialog.SetTitleAsync("WebSocket Resilience Session");

            var detail = await dialog.SubmitAsync();
            await detail.WaitForLoadedAsync();

            var sessionUri = new Uri(Page.Url);
            var sessionId = sessionUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();
            var instanceId = GetRequiredQueryValue(sessionUri, "instanceId");

            var tracker = _factory.KestrelServices.GetRequiredService<InstanceTracker>();
            var harness = tracker.Get(instanceId).ShouldBeOfType<TestHarnessSession>();
            var harnessSessionId = harness.InstanceId;

            const string onlineText = "Message received before disconnect";
            const string catchUpText = "Recovered after reconnect without page reload";

            await PushDurableAssistantMessageAsync(
                harness,
                harnessSessionId,
                sessionId,
                "msg-ws-online-1",
                onlineText);

            await detail.WaitForMessageTextAsync(onlineText, 10_000);

            var initialDocumentMarker = await Page.EvaluateAsync<string>("""
                () => {
                  window.__wsResilienceMarker ??= crypto.randomUUID();
                  return window.__wsResilienceMarker;
                }
                """);

            await Page.EvaluateAsync("window.__WEAVE_SOCKET_TEST_API?.suspend()")
                .ConfigureAwait(false);

            var disconnectedIndicator = Page.GetByTestId("session-status-indicator");
            await Assertions.Expect(disconnectedIndicator).ToHaveAttributeAsync(
                "data-status",
                "disconnected",
                new LocatorAssertionsToHaveAttributeOptions { Timeout = 10_000 });

            var disconnectedBanner = Page.GetByTestId("session-stopped-banner");
            await Assertions.Expect(disconnectedBanner).ToContainTextAsync(
                "Connection to this session was lost",
                new LocatorAssertionsToContainTextOptions { Timeout = 10_000 });

            await PushDurableAssistantMessageAsync(
                harness,
                harnessSessionId,
                sessionId,
                "msg-ws-catchup-1",
                catchUpText);

            await Assertions.Expect(Page.GetByTestId("message-item").Filter(new LocatorFilterOptions { HasText = catchUpText }))
                .ToHaveCountAsync(0, new LocatorAssertionsToHaveCountOptions { Timeout = 1_000 });

            await Page.EvaluateAsync("window.__WEAVE_SOCKET_TEST_API?.resume()")
                .ConfigureAwait(false);

            await detail.WaitForMessageTextAsync(catchUpText, 15_000);
            await Assertions.Expect(disconnectedIndicator).ToHaveAttributeAsync(
                "data-status",
                "idle",
                new LocatorAssertionsToHaveAttributeOptions { Timeout = 10_000 });
            await Assertions.Expect(disconnectedBanner).ToHaveCountAsync(0, new LocatorAssertionsToHaveCountOptions { Timeout = 10_000 });

            var finalDocumentMarker = await Page.EvaluateAsync<string>("() => window.__wsResilienceMarker");
            finalDocumentMarker.ShouldBe(initialDocumentMarker);
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
        var value = System.Web.HttpUtility.ParseQueryString(uri.Query)[key];
        value.ShouldNotBeNullOrWhiteSpace();
        return value;
    }
}

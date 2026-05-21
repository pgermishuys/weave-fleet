using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Harnesses;
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
    private const string StreamingPrompt = "Please stream a reliability response";
    private const string FirstStreamingChunk = "Streaming reliability";
    private const string FullStreamingResponse = "Streaming reliability response survives reconnect.";
    private const string StreamingMessageId = "msg-stream-disconnect-1";
    private const string StreamingPartId = "part-stream-disconnect-1";
    private const string OrderedPrompt = "Prompt ordering reliability check";
    private const string OrderedAssistantResponse = "Assistant response remains after the prompt.";
    private const string OrderedUserEchoMessageId = "msg-ordering-user-echo-1";
    private const string OrderedAssistantMessageId = "msg-ordering-assistant-1";
    private const string OrderedAssistantPartId = "part-ordering-assistant-1";
    private static readonly DateTimeOffset OrderedScenarioTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(4_000_000_000_000);

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

    [Fact]
    public async Task should_recover_full_assistant_response_and_keep_user_prompt_once_when_client_disconnects_during_streaming()
    {
        await WithFailureCapture(async () =>
        {
            ConfigureScenario(builder => builder.WithPromptResponse(response => response
                .AddEvent(MakeHarnessEvent(
                    "session.status",
                    new { sessionId = "_placeholder_", status = new { type = "busy" } }))
                .AddEvent(MakeHarnessEvent(
                    "message.updated",
                    new
                    {
                        info = new
                        {
                            id = StreamingMessageId,
                            sessionID = "_placeholder_",
                            role = "assistant",
                            time = new { created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
                            agent = "loom",
                        },
                    }),
                    TimeSpan.FromMilliseconds(50))
                .AddEvent(MakeHarnessEvent(
                    "message.part.delta",
                    new
                    {
                        sessionID = "_placeholder_",
                        messageID = StreamingMessageId,
                        partID = StreamingPartId,
                        field = "text",
                        delta = FirstStreamingChunk,
                    }),
                    TimeSpan.FromMilliseconds(100))
                .AddEvent(MakeHarnessEvent(
                    "message.part.delta",
                    new
                    {
                        sessionID = "_placeholder_",
                        messageID = StreamingMessageId,
                        partID = StreamingPartId,
                        field = "text",
                        delta = " response",
                    }),
                    TimeSpan.FromMilliseconds(450))
                .AddEvent(MakeHarnessEvent(
                    "message.part.delta",
                    new
                    {
                        sessionID = "_placeholder_",
                        messageID = StreamingMessageId,
                        partID = StreamingPartId,
                        field = "text",
                        delta = " survives reconnect.",
                    }),
                    TimeSpan.FromMilliseconds(450))
                .AddEvent(MakeHarnessEvent(
                    "message.updated",
                    new
                    {
                        info = new
                        {
                            id = StreamingMessageId,
                            sessionID = "_placeholder_",
                            role = "assistant",
                            time = new
                            {
                                created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                                completed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 1,
                            },
                            agent = "loom",
                        },
                        parts = new[]
                        {
                            new
                            {
                                id = StreamingPartId,
                                sessionID = "_placeholder_",
                                messageID = StreamingMessageId,
                                type = "text",
                                text = FullStreamingResponse,
                            },
                        },
                    }),
                    TimeSpan.FromMilliseconds(450))
                .AddEvent(MakeHarnessEvent(
                    "session.status",
                    new { sessionId = "_placeholder_", status = new { type = "idle" } }),
                    TimeSpan.FromMilliseconds(50))
                .AddEvent(MakeHarnessEvent(
                    "session.idle",
                    new { sessionId = "_placeholder_" }),
                    TimeSpan.FromMilliseconds(50))));

            await Page.AddInitScriptAsync("window.localStorage.removeItem('weave_v2_stream')");

            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();

            var dialog = await dashboard.ClickNewSessionAsync();
            await dialog.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
            await dialog.SetTitleAsync("Streaming disconnect reliability session");

            var detail = await dialog.SubmitAsync();
            await detail.WaitForLoadedAsync();

            var sessionUri = new Uri(Page.Url);
            var sessionId = sessionUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();

            await detail.SendPromptAsync(StreamingPrompt, 30_000);

            var firstAssistantChunk = detail.GetMessagesByRole("assistant")
                .Filter(new LocatorFilterOptions { HasText = FirstStreamingChunk });
            await Assertions.Expect(firstAssistantChunk).ToHaveCountAsync(
                1,
                new LocatorAssertionsToHaveCountOptions { Timeout = 15_000 });

            await Page.EvaluateAsync("window.__WEAVE_SOCKET_TEST_API?.suspend()").ConfigureAwait(false);

            var disconnectedIndicator = Page.GetByTestId("session-status-indicator");
            await Assertions.Expect(disconnectedIndicator).ToHaveAttributeAsync(
                "data-status",
                "disconnected",
                new LocatorAssertionsToHaveAttributeOptions { Timeout = 10_000 });

            // Deterministic prerequisite for the recovery assertion: the final durable
            // message.updated has reached the read model while the browser socket is suspended.
            await WaitForRetrievedMessageTextAsync(sessionId, FullStreamingResponse, TimeSpan.FromSeconds(30));

            await Page.EvaluateAsync("window.__WEAVE_SOCKET_TEST_API?.resume()").ConfigureAwait(false);

            await detail.WaitForMessageTextAsync(FullStreamingResponse, 30_000);

            var userPromptMessages = detail.GetMessagesByRole("user")
                .Filter(new LocatorFilterOptions { HasText = StreamingPrompt });
            await Assertions.Expect(userPromptMessages).ToHaveCountAsync(
                1,
                new LocatorAssertionsToHaveCountOptions { Timeout = 10_000 });
        });
    }

    [Fact]
    public async Task should_keep_exactly_one_user_prompt_live_and_after_reload_when_harness_echoes_created_and_updated_user_events()
    {
        await WithFailureCapture(async () =>
        {
            ConfigureScenario(builder => builder.WithPromptResponse(response => response
                .AddEvent(MakeHarnessEventAt(
                    "session.status",
                    new { sessionId = "_placeholder_", status = new { type = "busy" } },
                    OrderedScenarioTimestamp))
                .AddEvent(MakeHarnessEventAt(
                    "message.created",
                    new
                    {
                        info = new
                        {
                            id = OrderedUserEchoMessageId,
                            sessionID = "_placeholder_",
                            role = "user",
                            time = new { created = OrderedScenarioTimestamp.ToUnixTimeMilliseconds() - 1_000 },
                        },
                    },
                    OrderedScenarioTimestamp.AddMilliseconds(5)))
                .AddEvent(MakeHarnessEventAt(
                    "message.updated",
                    new
                    {
                        info = new
                        {
                            id = OrderedUserEchoMessageId,
                            sessionID = "_placeholder_",
                            role = "user",
                            time = new { created = OrderedScenarioTimestamp.ToUnixTimeMilliseconds() - 1_000 },
                        },
                    },
                    OrderedScenarioTimestamp.AddMilliseconds(10)))
                .AddEvent(MakeHarnessEventAt(
                    "message.part.updated",
                    new
                    {
                        sessionID = "_placeholder_",
                        part = new
                        {
                            type = "text",
                            id = $"{OrderedUserEchoMessageId}-part-1",
                            sessionID = "_placeholder_",
                            messageID = OrderedUserEchoMessageId,
                            text = OrderedPrompt,
                        },
                    },
                    OrderedScenarioTimestamp.AddMilliseconds(20)))
                .AddEvent(MakeHarnessEventAt(
                    "message.updated",
                    new
                    {
                        info = new
                        {
                            id = OrderedAssistantMessageId,
                            sessionID = "_placeholder_",
                            role = "assistant",
                            time = new { created = OrderedScenarioTimestamp.ToUnixTimeMilliseconds() + 1_000 },
                            agent = "loom",
                        },
                    },
                    OrderedScenarioTimestamp.AddMilliseconds(250)),
                    TimeSpan.FromMilliseconds(250))
                .AddEvent(MakeHarnessEventAt(
                    "message.part.updated",
                    new
                    {
                        sessionID = "_placeholder_",
                        part = new
                        {
                            type = "text",
                            id = OrderedAssistantPartId,
                            sessionID = "_placeholder_",
                            messageID = OrderedAssistantMessageId,
                            text = OrderedAssistantResponse,
                        },
                    },
                    OrderedScenarioTimestamp.AddMilliseconds(300)),
                    TimeSpan.FromMilliseconds(50))
                .AddEvent(MakeHarnessEventAt(
                    "message.updated",
                    new
                    {
                        info = new
                        {
                            id = OrderedAssistantMessageId,
                            sessionID = "_placeholder_",
                            role = "assistant",
                            time = new
                            {
                                created = OrderedScenarioTimestamp.ToUnixTimeMilliseconds() + 1_000,
                                completed = OrderedScenarioTimestamp.ToUnixTimeMilliseconds() + 1_500,
                            },
                            agent = "loom",
                        },
                        parts = new[]
                        {
                            new
                            {
                                id = OrderedAssistantPartId,
                                sessionID = "_placeholder_",
                                messageID = OrderedAssistantMessageId,
                                type = "text",
                                text = OrderedAssistantResponse,
                            },
                        },
                    },
                    OrderedScenarioTimestamp.AddMilliseconds(350)),
                    TimeSpan.FromMilliseconds(50))
                .AddEvent(MakeHarnessEventAt(
                    "session.status",
                    new { sessionId = "_placeholder_", status = new { type = "idle" } },
                    OrderedScenarioTimestamp.AddMilliseconds(400)),
                    TimeSpan.FromMilliseconds(50))
                .AddEvent(MakeHarnessEventAt(
                    "session.idle",
                    new { sessionId = "_placeholder_" },
                    OrderedScenarioTimestamp.AddMilliseconds(450)),
                    TimeSpan.FromMilliseconds(50))));

            await Page.AddInitScriptAsync("window.localStorage.removeItem('weave_v2_stream')");

            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();

            var dialog = await dashboard.ClickNewSessionAsync();
            await dialog.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
            await dialog.SetTitleAsync("Prompt response ordering reliability session");

            var detail = await dialog.SubmitAsync();
            await detail.WaitForLoadedAsync();

            await detail.SendPromptAsync(OrderedPrompt, 30_000);
            await detail.WaitForMessageTextAsync(OrderedAssistantResponse, 30_000);
            await detail.WaitForIdleAsync(30_000);

            // User echo events from the harness are suppressed before client replay/harness_events
            // logging so they cannot duplicate the send-time prompt bubble. The UI should still
            // show the single prompt that was persisted at send time while assistant events flow.
            await AssertPromptBeforeAssistantWithoutEchoDuplicateAsync(detail);

            await Page.ReloadAsync();
            await detail.WaitForLoadedAsync();
            await detail.WaitForMessageTextAsync(OrderedPrompt, 30_000);
            await detail.WaitForMessageTextAsync(OrderedAssistantResponse, 30_000);

            // Reload exercises the persisted session snapshot/reconnect path: the snapshot must
            // contain exactly one user prompt even though message.created and message.updated
            // harness echoes were observed for role=user during the live stream.
            await AssertPromptBeforeAssistantWithoutEchoDuplicateAsync(detail);
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

    private static HarnessEvent MakeHarnessEvent(string type, object payload)
        => new()
        {
            Type = type,
            SessionId = "_placeholder_",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(payload),
        };

    private static HarnessEvent MakeHarnessEventAt(string type, object payload, DateTimeOffset timestamp)
        => new()
        {
            Type = type,
            SessionId = "_placeholder_",
            Timestamp = timestamp,
            Payload = JsonSerializer.SerializeToElement(payload),
        };

    private async Task AssertPromptBeforeAssistantWithoutEchoDuplicateAsync(SessionDetailPage detail)
    {
        await Assertions.Expect(detail.GetMessagesByRole("user")).ToHaveCountAsync(
            1,
            new LocatorAssertionsToHaveCountOptions { Timeout = 10_000 });

        var messageOrder = await Page.EvaluateAsync<RenderedMessage[]>("""
            () => Array.from(document.querySelectorAll('[data-testid="message-item"]'))
              .map((element) => ({
                role: element.getAttribute('data-role') ?? '',
                text: element.textContent ?? '',
              }))
            """);

        var promptIndex = FindMessageIndex(messageOrder, "user", OrderedPrompt);
        var assistantIndex = FindMessageIndex(messageOrder, "assistant", OrderedAssistantResponse);

        promptIndex.ShouldBeGreaterThanOrEqualTo(0, "Expected the persisted user prompt bubble to be rendered.");
        assistantIndex.ShouldBeGreaterThanOrEqualTo(0, "Expected the controlled assistant response bubble to be rendered.");
        promptIndex.ShouldBeLessThan(assistantIndex, "Expected the user prompt to render before the assistant response.");
    }

    private static int FindMessageIndex(IReadOnlyList<RenderedMessage> messages, string role, string text)
    {
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            if (string.Equals(message.Role, role, StringComparison.Ordinal)
                && message.Text.Contains(text, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private sealed class RenderedMessage
    {
        public string Role { get; init; } = string.Empty;

        public string Text { get; init; } = string.Empty;
    }

    private async Task WaitForRetrievedMessageTextAsync(
        string sessionId,
        string expectedText,
        TimeSpan timeout)
    {
        using var httpClient = new HttpClient { BaseAddress = new Uri(ServerUrl) };
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            using (var response = await httpClient.GetAsync(
                $"/api/sessions/{Uri.EscapeDataString(sessionId)}/messages").ConfigureAwait(false))
            {
                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (body.Contains(expectedText, StringComparison.Ordinal))
                        return;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"Expected retrieved session messages to contain '{expectedText}' within {timeout}.");
    }

    private static string GetRequiredQueryValue(Uri uri, string key)
    {
        var value = System.Web.HttpUtility.ParseQueryString(uri.Query)[key];
        value.ShouldNotBeNullOrWhiteSpace();
        return value;
    }
}

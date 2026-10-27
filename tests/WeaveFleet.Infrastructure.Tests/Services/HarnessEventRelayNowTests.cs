using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Events;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Services;

/// <summary>The relay keeps the now line only once a client has asked for it; before that it never calls the tracker.</summary>
public sealed class HarnessEventRelayNowTests
{
    private static HarnessEvent ToolEvent(string command) => new()
    {
        Type = EventTypes.MessagePartUpdated,
        SessionId = "oc-1",
        Timestamp = DateTimeOffset.UtcNow,
        Payload = JsonSerializer.SerializeToElement(new
        {
            sessionID = "oc-1",
            part = new
            {
                type = "tool", id = "part-1", messageID = "msg-1", sessionID = "oc-1", tool = "bash", callID = "call-1",
                state = new { status = "running", input = new { command } },
            },
        }),
    };

    [Fact]
    public async Task Nothing_is_kept_until_a_client_asks_and_then_the_latest_tool_call_is()
    {
        var sessions = new InMemorySessionRepository();
        sessions.Seed(new Session { Id = "fleet-1", InstanceId = "instance-1", UserId = "user-1", HarnessType = "test" });
        var scopeFactory = TestServiceScopeFactory.Create(services =>
        {
            services.AddLogging();
            services.AddSingleton<ISessionRepository>(sessions);
            services.AddTransient<DomainEventTranslator>();
        });
        var publisher = new FakeEventPublisher();
        var instances = new InstanceTracker();
        var now = new SessionNowTracker();
        using var relay = new HarnessEventRelay(
            instances, new FakeEventBroadcaster(), publisher, new SessionActivityTracker(), scopeFactory,
            NullLogger<HarnessEventRelay>.Instance, now: now);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await relay.StartAsync(cts.Token);
        var harness = new FakeHarnessSession("instance-1") { HarnessType = "test" };
        instances.Register("instance-1", harness);

        harness.Emit(ToolEvent("pnpm lint"));
        await IdleReachedAsync(harness, publisher, cts.Token);
        now.GetFor("user-1").ShouldBeEmpty();

        now.Activate();
        harness.Emit(ToolEvent("pnpm test webhooks"));
        await IdleReachedAsync(harness, publisher, cts.Token);

        now.GetFor("user-1")["fleet-1"].Text.ShouldBe("bash · pnpm test webhooks");

        harness.Complete();
        await cts.CancelAsync();
        await relay.StopAsync(CancellationToken.None);
    }

    // The pump takes events in order, so once an idle sent after the tool call is published, the call has been seen.
    private static async Task IdleReachedAsync(FakeHarnessSession harness, FakeEventPublisher publisher, CancellationToken ct)
    {
        var before = publisher.Calls.Count(c => c.Event.Type == EventTypes.SessionIdle);
        harness.Emit(new HarnessEvent { Type = EventTypes.SessionIdle, SessionId = "oc-1", Timestamp = DateTimeOffset.UtcNow });
        while (publisher.Calls.Count(c => c.Event.Type == EventTypes.SessionIdle) == before)
            await Task.Delay(10, ct);
    }
}

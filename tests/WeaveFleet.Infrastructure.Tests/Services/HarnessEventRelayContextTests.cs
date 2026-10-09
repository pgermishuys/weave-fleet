using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Events;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Events;
using WeaveFleet.Infrastructure.Harnesses;
using WeaveFleet.Infrastructure.Services;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Services;

/// <summary>
/// The relay hands what a harness says about its session's context to Fleet's record of it
/// (<see cref="SessionContextRecorder"/>), which tells clients itself; the conversation never sees those events. The
/// session going idle ends the turn there too.
/// </summary>
public sealed class HarnessEventRelayContextTests : IDisposable
{
    private const string Session = "fleet-session";

    private readonly InMemorySessionRepository _sessions = new();
    private readonly InMemorySessionContextRepository _contexts = new();
    private readonly FakeEventBroadcaster _broadcaster = new();
    private readonly FakeEventPublisher _publisher = new();
    private readonly InstanceTracker _tracker = new();
    private readonly SessionContextRecorder _recorder;
    private readonly HarnessEventRelay _relay;

    public HarnessEventRelayContextTests()
    {
        _sessions.Seed(new Session { Id = Session, InstanceId = "instance-1", UserId = "user-1", HarnessType = "opencode" });
        var scopeFactory = TestServiceScopeFactory.Create(services =>
        {
            services.AddLogging();
            services.AddSingleton<ISessionRepository>(_sessions);
            services.AddSingleton<ISessionContextRepository>(_contexts);
            services.AddSingleton<IEventBroadcaster>(_broadcaster);
            services.AddSingleton<IUserContext>(new TestUserContext("user-1"));
            services.AddSingleton<IBackgroundUserScope>(new NoUserScope());
            services.AddScoped<SessionContextService>();
            services.AddTransient<DomainEventTranslator>();
        });
        _recorder = new SessionContextRecorder(scopeFactory, NullLogger<SessionContextRecorder>.Instance);
        _relay = new HarnessEventRelay(
            _tracker, _broadcaster, _publisher, new SessionActivityTracker(), scopeFactory, NullLogger<HarnessEventRelay>.Instance,
            context: _recorder);
    }

    public void Dispose() => _relay.Dispose();

    [Fact]
    public async Task Context_reports_go_to_fleets_record_and_not_to_the_conversation()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await _relay.StartAsync(cts.Token);
        var harness = new FakeHarnessSession("instance-1");
        _tracker.Register(harness.InstanceId, harness);

        harness.Emit(new HarnessEvent
        {
            Type = EventTypes.SessionStatus,
            SessionId = "ses_1",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = System.Text.Json.JsonSerializer.SerializeToElement(new { status = new { type = "busy" } }),
        });
        harness.Emit(ContextEvents.Usage(new ContextUsageReport { Call = new ContextCall { Input = 40_000, Output = 500 }, Limit = 200_000, ModelId = "m" }, "ses_1"));
        harness.Emit(ContextEvents.Compaction(ContextCompactionPhases.Started, "ses_1", ContextCompactionTriggers.Auto));
        harness.Emit(ContextEvents.Usage(new ContextUsageReport { Call = new ContextCall { Input = 12_000, Output = 300 }, ModelId = "m" }, "ses_1"));
        harness.Emit(new HarnessEvent { Type = EventTypes.SessionIdle, SessionId = "ses_1", Timestamp = DateTimeOffset.UtcNow });

        await WaitUntilAsync(() => _publisher.Calls.Any(c => c.Event.Type == EventTypes.SessionIdle), cts.Token);
        await _recorder.Idle(Session).WaitAsync(cts.Token);

        _publisher.Calls.ShouldNotContain(c => EventTypes.IsContextEvent(c.Event.Type));
        var context = _contexts[Session].ShouldNotBeNull();
        context.Used.ShouldBe(12_300);
        context.Limit.ShouldBe(200_000);
        context.Compacting.ShouldBeFalse();
        context.Turns.ShouldHaveSingleItem().Used.ShouldBe(12_300);
        _broadcaster.Broadcasts.ShouldContain(b => b.Topic == $"session:{Session}" && b.Type == SessionContextUsage.UpdatedEventType);

        harness.Complete();
        await _relay.StopAsync(CancellationToken.None);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition())
            await Task.Delay(20, ct);
    }

    private sealed class NoUserScope : IBackgroundUserScope
    {
        public IDisposable Begin(string userId) => new Nothing();

        private sealed class Nothing : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}

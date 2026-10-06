using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Services;

public sealed class SessionContextRecorderTests
{
    private const string Session = "session-1";
    private const string Owner = "user-1";

    private readonly InMemorySessionContextRepository _contexts = new();
    private readonly FakeEventBroadcaster _broadcaster = new();
    private readonly SessionContextRecorder _sut;

    public SessionContextRecorderTests()
    {
        _sut = new SessionContextRecorder(
            TestServiceScopeFactory.Create(services =>
            {
                services.AddSingleton<IBackgroundUserScope>(new NoUserScope());
                services.AddSingleton<IUserContext>(new TestUserContext(Owner));
                services.AddSingleton<ISessionContextRepository>(_contexts);
                services.AddSingleton<IEventBroadcaster>(_broadcaster);
                services.AddScoped<SessionContextService>();
            }),
            NullLogger<SessionContextRecorder>.Instance);
    }

    private static ContextUsageReport Usage(int input) => new() { Call = new ContextCall { Input = input, Output = 10 }, Limit = 100_000 };

    private static SessionIdled Idled() => new() { Payload = new SessionIdledPayload { SessionId = Session } };

    [Fact]
    public async Task records_calls_compactions_and_turn_ends_in_the_order_the_harness_sent_them()
    {
        _sut.Observe(Session, Owner, Usage(1_000));
        _sut.Observe(Session, Owner, Usage(2_000));
        _sut.Observe(Session, Owner, Idled());
        _sut.Observe(Session, Owner, new ContextCompactionReport { Phase = ContextCompactionPhases.Started });
        _sut.Observe(Session, Owner, new ContextCompactionReport { Phase = ContextCompactionPhases.Ended });
        await _sut.Idle(Session);

        var context = _contexts[Session].ShouldNotBeNull();
        context.UserId.ShouldBe(Owner);
        context.Used.ShouldBeNull();
        context.CompactedAt.ShouldNotBeNull();
        context.Turns.ShouldHaveSingleItem().Used.ShouldBe(2_010);
    }

    [Fact]
    public async Task ignores_other_events_and_reports_without_an_owner()
    {
        _sut.Observe(Session, Owner, new TurnStarted { Payload = new TurnStartedPayload { SessionId = Session, MessageId = "msg-1", Index = 0 } });
        _sut.Observe(Session, null, Usage(1_000));
        await _sut.Idle(Session);

        _contexts[Session].ShouldBeNull();
        _broadcaster.Broadcasts.ShouldBeEmpty();
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

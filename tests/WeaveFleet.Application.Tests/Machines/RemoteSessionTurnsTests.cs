using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Application.Machines;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Events;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Application.Tests.Machines;

/// <summary>
/// A session here waits on one on another machine. Its events, as that machine's hub sends them, tell this Fleet when the
/// turn answering the message ends; a turn that ended while the connection was down is asked about instead, so the
/// update comes either way, once.
/// </summary>
public sealed class RemoteSessionTurnsTests : IDisposable
{
    private const string Remote = "s-remote";
    private static readonly SessionMessageMachine Atlas = new(FakeMachine.Id, FakeMachine.Name);

    private readonly FakeMachine _atlas = new();
    private readonly CapturingSender _sender = new();
    private readonly SessionUpdates _updates;
    private readonly RemoteSessionTurns _sut;
    private string _status = "busy";
    private string _messages = """{"messages":[{"id":"msg-1","role":"user","parts":[]}]}""";

    public RemoteSessionTurnsTests()
    {
        var machine = _atlas.Machines.GetAsync(FakeMachine.Id).GetAwaiter().GetResult()!;
        _atlas.Machines.UpsertAsync(machine with { AgentsAllowed = true }).GetAwaiter().GetResult();
        _atlas.Answer = request => request switch
        {
            "GET /api/sessions/s-remote" => (HttpStatusCode.OK, $$"""{"id":"s-remote","activityStatus":"{{_status}}"}"""),
            "GET /api/sessions/s-remote/messages" => (HttpStatusCode.OK, _messages),
            _ => null,
        };

        _updates = new SessionUpdates(
            new SessionActivityTracker(),
            TestServiceScopeFactory.Create(services =>
            {
                services.AddSingleton<IBackgroundUserScope>(new NoUserScope());
                services.AddSingleton<ISessionUpdateSender>(_sender);
            }),
            NullLogger<SessionUpdates>.Instance);
        _updates.Watch(new SessionUpdateWatch("s-asker", Remote, "user-1", "msg-1", Atlas, "Run the integration tests"));
        _sut = new RemoteSessionTurns(_updates, new RemoteSessions(_atlas.Service, _atlas));
    }

    public void Dispose() => _atlas.Dispose();

    [Fact]
    public async Task Its_reply_and_going_idle_tell_the_session_that_asked_and_end_the_following()
    {
        _sut.Observe(Remote, Reply("reply-1", parent: "msg-1")).ShouldBeFalse();
        var done = _sut.Observe(Remote, Idle());
        await _updates.Pending;

        done.ShouldBeTrue();
        _sender.Read.ShouldHaveSingleItem().Failure.ShouldBeNull();
    }

    [Fact]
    public async Task A_turn_already_running_when_the_message_came_doesnt_count()
    {
        _sut.Observe(Remote, Reply("reply-0", parent: "msg-0"));
        var done = _sut.Observe(Remote, Idle());
        await _updates.Pending;

        done.ShouldBeFalse();
        _sender.Read.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_reply_that_ended_while_the_connection_was_down_is_delivered_when_it_is_back()
    {
        _status = "idle";
        _messages = """
            {"messages":[
              {"id":"msg-1","role":"user","parts":[]},
              {"id":"reply-1","role":"assistant","parts":[{"type":"text","text":"2 failed."}],"finish":"stop"}
            ]}
            """;

        var done = await _sut.CatchUpAsync(FakeMachine.Id, Remote);
        await _updates.Pending;

        done.ShouldBeTrue();
        var (watch, failure) = _sender.Read.ShouldHaveSingleItem();
        watch.MessageId.ShouldBe("msg-1");
        failure.ShouldBeNull();

        // Its events can still turn up afterwards; it's told once.
        _sut.Observe(Remote, Idle());
        await _updates.Pending;
        _sender.Read.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_turn_that_failed_while_the_connection_was_down_says_why()
    {
        _status = "idle";
        _messages = """
            {"messages":[
              {"id":"msg-1","role":"user","parts":[]},
              {"id":"reply-1","role":"assistant","parts":[],"error":{"name":"RateLimit","message":"Rate limited."}}
            ]}
            """;

        (await _sut.CatchUpAsync(FakeMachine.Id, Remote)).ShouldBeTrue();
        await _updates.Pending;

        _sender.Read.ShouldHaveSingleItem().Failure!.Message.ShouldBe("Rate limited.");
    }

    [Fact]
    public async Task A_turn_cut_off_when_Fleet_there_stopped_says_it_stopped()
    {
        _status = "idle";
        _messages = """{"messages":[{"id":"msg-1","role":"user","parts":[]},{"id":"reply-1","role":"assistant","parts":[]}]}""";

        (await _sut.CatchUpAsync(FakeMachine.Id, Remote)).ShouldBeTrue();
        await _updates.Pending;

        _sender.Read.ShouldHaveSingleItem().Failure!.Message.ShouldBe(RemoteSessions.StoppedMessage(FakeMachine.Name));
    }

    [Theory]
    [InlineData("busy", true)]
    [InlineData("idle", false)]
    public async Task A_turn_still_going_or_not_started_waits_for_its_events(string status, bool replied)
    {
        _status = status;
        if (replied)
        {
            _messages = """{"messages":[{"id":"msg-1","role":"user","parts":[]},{"id":"reply-1","role":"assistant","parts":[]}]}""";
        }

        (await _sut.CatchUpAsync(FakeMachine.Id, Remote)).ShouldBeFalse();
        await _updates.Pending;
        _sender.Read.ShouldBeEmpty();

        _sut.Observe(Remote, Reply("reply-1", parent: "msg-1"));
        _sut.Observe(Remote, Idle()).ShouldBeTrue();
        await _updates.Pending;
        _sender.Read.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_machine_that_doesnt_answer_keeps_it_waiting()
    {
        _atlas.Away = true;

        (await _sut.CatchUpAsync(FakeMachine.Id, Remote)).ShouldBeFalse();
        _updates.IsWatching(Remote).ShouldBeTrue();
    }

    private static MessageUpdated Reply(string id, string parent) => new()
    {
        Payload = new MessageLifecyclePayload
        {
            Info = new MessageEventInfo { Id = id, Role = "assistant", SessionId = Remote, ParentId = parent, Time = new MessageEventTime { Created = 0 } },
        },
    };

    private static SessionIdled Idle() => new() { Payload = new SessionIdledPayload { SessionId = Remote } };

    private sealed class CapturingSender : ISessionUpdateSender
    {
        public List<(SessionUpdateWatch Watch, TurnError? Failure)> Read { get; } = [];

        public Task<SessionUpdate?> ReadAsync(SessionUpdateWatch watch, TurnError? failure, CancellationToken ct)
        {
            lock (Read)
                Read.Add((watch, failure));
            return Task.FromResult<SessionUpdate?>(null);
        }

        public Task<bool> SendAsync(SessionUpdate update, CancellationToken ct) => Task.FromResult(true);
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

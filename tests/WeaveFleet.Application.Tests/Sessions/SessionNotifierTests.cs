using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Sessions;

public sealed class SessionNotifierTests
{
    private const string SessionId = "s1";
    private const string UserId = "u1";

    private readonly FakeEventBroadcaster _broadcaster = new();
    private readonly SessionFocusTracker _focus = new();
    private readonly FakeNotificationPreference _preference = new() { Enabled = true };
    private readonly InMemorySessionRepository _sessions = new();
    private readonly SessionNotifier _sut;

    public SessionNotifierTests()
    {
        _sessions.InsertAsync(new Session
        {
            Id = SessionId,
            InstanceId = "inst-1",
            HarnessType = "opencode",
            UserId = UserId,
            Title = "Make the dashboard true",
        }).GetAwaiter().GetResult();

        _sut = new SessionNotifier(
            _focus,
            _broadcaster,
            _preference,
            TestServiceScopeFactory.Create(services => services.AddSingleton<ISessionRepository>(_sessions)),
            NullLogger<SessionNotifier>.Instance);
    }

    [Fact]
    public async Task tells_you_when_a_session_you_left_stops_on_a_question()
    {
        await ChangeAsync(ActivityStatuses.Busy);
        await ChangeAsync(ActivityStatuses.WaitingInput);

        var sent = Single();
        sent.Topic.ShouldBe("sessions");
        sent.Type.ShouldBe(SessionNotifier.EventType);
        sent.UserId.ShouldBe(UserId);
        Payload(sent).Reason.ShouldBe(SessionNotificationReasons.NeedsYou);
        Payload(sent).Title.ShouldBe("Make the dashboard true");
        Payload(sent).SessionId.ShouldBe(SessionId);
    }

    [Fact]
    public async Task tells_you_when_a_turn_you_left_ends()
    {
        await ChangeAsync(ActivityStatuses.Busy);
        await ChangeAsync(ActivityStatuses.Idle);

        Payload(Single()).Reason.ShouldBe(SessionNotificationReasons.Finished);
    }

    [Fact]
    public async Task says_nothing_about_the_session_you_are_looking_at()
    {
        _focus.SetFocus("tab-1", SessionId, focused: true);

        await ChangeAsync(ActivityStatuses.Busy);
        await ChangeAsync(ActivityStatuses.WaitingInput);
        await ChangeAsync(ActivityStatuses.Busy);
        await ChangeAsync(ActivityStatuses.Idle);

        _broadcaster.Broadcasts.ShouldBeEmpty();
    }

    [Fact]
    public async Task says_nothing_about_an_idle_session_it_never_saw_working()
    {
        // Everything Fleet knows about a session starts at its next event: a turn that ended before
        // that, or a status resent for another reason, is not news.
        await ChangeAsync(ActivityStatuses.Idle);
        await ChangeAsync(ActivityStatuses.Idle);

        _broadcaster.Broadcasts.ShouldBeEmpty();
    }

    [Fact]
    public async Task says_nothing_when_the_setting_is_off()
    {
        _preference.Enabled = false;

        await ChangeAsync(ActivityStatuses.Busy);
        await ChangeAsync(ActivityStatuses.WaitingInput);

        _broadcaster.Broadcasts.ShouldBeEmpty();
    }

    [Fact]
    public async Task says_nothing_about_a_subagent()
    {
        await _sessions.InsertAsync(new Session
        {
            Id = "child",
            InstanceId = "inst-1",
            HarnessType = "opencode",
            UserId = UserId,
            ParentSessionId = SessionId,
        });

        await ChangeAsync(ActivityStatuses.Busy, "child");
        await ChangeAsync(ActivityStatuses.Idle, "child");

        _broadcaster.Broadcasts.ShouldBeEmpty();
    }

    [Fact]
    public async Task says_nothing_about_an_archived_session()
    {
        var session = await _sessions.GetByIdAsync(SessionId);
        session!.RetentionStatus = "archived";

        await ChangeAsync(ActivityStatuses.Busy);
        await ChangeAsync(ActivityStatuses.Idle);

        _broadcaster.Broadcasts.ShouldBeEmpty();
    }

    [Fact]
    public async Task does_not_repeat_itself_while_the_status_holds()
    {
        await ChangeAsync(ActivityStatuses.Busy);
        await ChangeAsync(ActivityStatuses.WaitingInput);
        await ChangeAsync(ActivityStatuses.WaitingInput);

        _broadcaster.Broadcasts.Count.ShouldBe(1);
    }

    [Fact]
    public async Task forgetting_a_session_means_its_next_idle_is_not_a_finished_turn()
    {
        await ChangeAsync(ActivityStatuses.Busy);
        _sut.Forget(SessionId);

        await ChangeAsync(ActivityStatuses.Idle);

        _broadcaster.Broadcasts.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_turn_it_only_learns_about_on_reconnect_still_counts_as_finished()
    {
        // The relay forgets a session when its pump ends and re-reads the harness when a new one starts.
        await ChangeAsync(ActivityStatuses.Busy);
        _sut.Forget(SessionId);
        await ChangeAsync(ActivityStatuses.Busy);

        await ChangeAsync(ActivityStatuses.Idle);

        Payload(Single()).Reason.ShouldBe(SessionNotificationReasons.Finished);
    }

    private async Task ChangeAsync(string activityStatus, string sessionId = SessionId)
    {
        _sut.OnActivityChanged(sessionId, activityStatus);

        // The notification is written off the caller's thread; give it a turn of the loop to land.
        for (var i = 0; i < 20 && _broadcaster.Broadcasts.Count == 0; i++)
            await Task.Delay(5);
    }

    private FakeEventBroadcaster.BroadcastRecord Single()
    {
        _broadcaster.Broadcasts.Count.ShouldBe(1);
        return _broadcaster.Broadcasts[0];
    }

    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    private static SessionNotificationPayload Payload(FakeEventBroadcaster.BroadcastRecord record)
        => record.Payload.Deserialize<SessionNotificationPayload>(PayloadJson)!;

    private sealed class FakeNotificationPreference : INotificationPreference
    {
        public bool Enabled { get; set; }

        public Task<bool> IsEnabledAsync(string userId, CancellationToken ct) => Task.FromResult(Enabled);
    }
}

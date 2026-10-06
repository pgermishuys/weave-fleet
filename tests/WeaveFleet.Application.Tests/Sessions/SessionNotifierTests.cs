using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
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
    private readonly FakePendingPermissions _pending = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.Zero));
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
            [new BroadcastNotificationSink(_broadcaster)],
            TestServiceScopeFactory.Create(services => services.AddSingleton<ISessionRepository>(_sessions)),
            NullLogger<SessionNotifier>.Instance,
            _pending,
            time: _time);
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
    public async Task a_permission_ask_is_a_permission_with_its_request_and_command()
    {
        _pending.Asks.Add(new PermissionAsk { Id = "perm-1", SessionId = SessionId, Kind = PermissionKinds.Shell, Tool = "bash", Title = "dotnet test" });

        await ChangeAsync(ActivityStatuses.Busy);
        await ChangeAsync(ActivityStatuses.WaitingInput);

        var payload = Payload(Single());
        payload.Kind.ShouldBe(SessionNotificationKinds.Permission);
        payload.RequestId.ShouldBe("perm-1");
        payload.Body.ShouldBe("Wants to run dotnet test");
        payload.Reason.ShouldBe(SessionNotificationReasons.NeedsYou);
    }

    [Fact]
    public async Task waiting_without_a_permission_ask_is_a_question()
    {
        await ChangeAsync(ActivityStatuses.Busy);
        await ChangeAsync(ActivityStatuses.WaitingInput);

        var payload = Payload(Single());
        payload.Kind.ShouldBe(SessionNotificationKinds.Question);
        payload.RequestId.ShouldBeNull();
    }

    [Fact]
    public async Task a_finished_turn_is_finished()
    {
        await ChangeAsync(ActivityStatuses.Busy);
        await ChangeAsync(ActivityStatuses.Idle);

        Payload(Single()).Kind.ShouldBe(SessionNotificationKinds.Finished);
    }

    [Fact]
    public async Task a_failed_turn_says_failed_and_not_finished_too()
    {
        await ChangeAsync(ActivityStatuses.Busy);
        _sut.OnSessionFailed(SessionId, "The model is overloaded.");
        await WaitForAsync(1);
        await ChangeAsync(ActivityStatuses.Idle);
        await Task.Delay(50);

        var payload = Payload(Single());
        payload.Kind.ShouldBe(SessionNotificationKinds.Failed);
        payload.Reason.ShouldBe(SessionNotificationReasons.Failed);
        payload.Body.ShouldBe("Stopped: The model is overloaded.");
    }

    [Fact]
    public async Task a_workflow_wait_is_a_workflow()
    {
        _sut.OnWorkflowNeedsYou(SessionId, "Review the plan.");
        await WaitForAsync(1);

        var payload = Payload(Single());
        payload.Kind.ShouldBe(SessionNotificationKinds.Workflow);
        payload.Body.ShouldBe("Review the plan.");
    }

    [Fact]
    public async Task the_same_ask_twice_within_thirty_seconds_is_sent_once()
    {
        _sut.OnWorkflowNeedsYou(SessionId, "Review the plan.");
        await WaitForAsync(1);
        _sut.OnWorkflowNeedsYou(SessionId, "Review the plan.");
        await Task.Delay(50);
        _broadcaster.Broadcasts.Count.ShouldBe(1);

        _time.Advance(SessionNotifier.RepeatWindow);
        _sut.OnWorkflowNeedsYou(SessionId, "Review the plan.");
        await WaitForAsync(2);
        _broadcaster.Broadcasts.Count.ShouldBe(2);
    }

    [Fact]
    public async Task long_bodies_are_cut_short()
    {
        _sut.OnWorkflowNeedsYou(SessionId, new string('x', 400));
        await WaitForAsync(1);

        Payload(Single()).Body.Length.ShouldBe(140);
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

    private async Task WaitForAsync(int count)
    {
        for (var i = 0; i < 40 && _broadcaster.Broadcasts.Count < count; i++)
            await Task.Delay(5);
    }

    private sealed class FakePendingPermissions : IPendingPermissions
    {
        public List<PermissionAsk> Asks { get; } = [];

        public IReadOnlyList<PermissionAsk> WaitingIn(string sessionId) => Asks.Where(a => a.SessionId == sessionId).ToList();
    }
}

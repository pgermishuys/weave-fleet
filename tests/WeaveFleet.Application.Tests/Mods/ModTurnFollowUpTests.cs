using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using WeaveFleet.Application.Mods;
using WeaveFleet.Application.Mods.Host;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Application.Tests.Mods.Host;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Testing.Builders;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Mods;

/// <summary>
/// <c>turn.complete</c> reaches the user's mods when a session's turn ends, before the queue's next message goes, and a
/// follow-up a mod queues in that time is the one sent.
/// </summary>
public sealed class ModTurnFollowUpTests : IAsyncDisposable
{
    private const string User = "user-1";

    private readonly TestUserContext _user = new(User);
    private readonly SessionOrchestratorBuilder _builder;
    private readonly FakeHarnessSession _harness = new("inst-1");
    private readonly InMemoryQueuedPromptRepository _queue = new();
    private readonly ModPromptHookTests.ScriptedHost _host = new();
    private readonly SignalingTime _time = new();
    private readonly ModFollowUps _followUps = new();
    private readonly ModTurnHook _turns;
    private readonly PromptQueueService _service;
    private readonly PromptQueueDispatcher _dispatcher;

    public ModTurnFollowUpTests()
    {
        _builder = new SessionOrchestratorBuilder().WithUserContext(_user);
        _builder.RegisterHarness("opencode", "OpenCode", new HarnessCapabilities());
        Seed("s1", "/work/demo");
        _builder.InstanceTracker.Register("inst-1", _harness);
        var orchestrator = _builder.Build();
        _service = new PromptQueueService(
            _builder.Prompting, _builder.ShellCommands, _builder.SessionRepository, _queue, _builder.EventBroadcaster,
            _builder.ActivityTracker, _builder.HarnessRegistry, _user,
            new TurnRetryScheduler(TestServiceScopeFactory.CreateEmpty(), TimeProvider.System, NullLogger<TurnRetryScheduler>.Instance),
            NullLogger<PromptQueueService>.Instance);
        _turns = new ModTurnHook(_host, _followUps, _time, NullLogger<ModTurnHook>.Instance);
        var scopes = TestServiceScopeFactory.Create(services =>
        {
            services.AddSingleton<IBackgroundUserScope>(new NoUserScope());
            services.AddSingleton(orchestrator);
            services.AddSingleton<ISessionRepository>(_builder.SessionRepository);
            services.AddSingleton(_service);
        });
        _dispatcher = new PromptQueueDispatcher(scopes, NullLogger<PromptQueueDispatcher>.Instance, _turns);
        _builder.ActivityTracker.Update("s1", ActivityStatuses.Idle, User);
    }

    public ValueTask DisposeAsync() => _harness.DisposeAsync();

    private void Seed(string id, string directory, string retention = "active", string? sideOf = null, string? parent = null)
        => _builder.SessionRepository.Seed(new Session
        {
            Id = id, InstanceId = "inst-1", Title = "T", Status = "active", Directory = directory, CreatedAt = "2026-01-01",
            RetentionStatus = retention, HarnessType = "opencode", UserId = User, SideOfSessionId = sideOf, ParentSessionId = parent,
        });

    private static FilesWritten Wrote(params string[] paths) => new() { Payload = new FilesWrittenPayload { SessionId = "s1", Paths = paths } };

    private static TurnEnded Ended(string messageId) => new() { Payload = new TurnEndedPayload { SessionId = "s1", MessageId = messageId, Index = 0 } };

    private static TurnFailed Failed(string message) => new() { Payload = new TurnFailedPayload { SessionId = "s1", Error = new TurnError { Name = "APIError", Message = message } } };

    private static SessionIdled Idled() => new() { Payload = new SessionIdledPayload { SessionId = "s1" } };

    private Task TurnEnds(string session = "s1")
    {
        _dispatcher.Observe(session, User, Idled());
        return _dispatcher.Pending.Within();
    }

    /// <summary>Queues as the user would mid-turn, then the turn is over and the session idle.</summary>
    private async Task QueueWhileBusyAsync(string text)
    {
        _builder.ActivityTracker.Update("s1", ActivityStatuses.Busy, User);
        await _service.EnqueueAsync("s1", new QueuePromptRequest(text, QueuedPromptKinds.Prompt));
        _builder.ActivityTracker.Update("s1", ActivityStatuses.Idle, User);
    }

    private string[] Sent => [.. _harness.SendPromptCalls.Select(c => c.Text)];

    [Fact]
    public async Task The_turn_ends_with_the_files_it_changed_and_a_turn_with_none_sends_an_empty_list()
    {
        _dispatcher.Observe("s1", User, Wrote("/work/demo/src/berths.ts"));
        _dispatcher.Observe("s1", User, Wrote("/work/demo/src/berths.ts", "/etc/hosts"));
        _dispatcher.Observe("s1", User, Ended("msg_1"));
        _dispatcher.Observe("s1", User, Ended("msg_2"));

        await TurnEnds();
        await TurnEnds();

        _host.Requests.Select(r => (r.User, r.Request.Event, r.Request.SessionId)).ShouldBe([(User, "turn.complete", "s1"), (User, "turn.complete", "s1")]);
        _host.Requests[0].Request.E.GetRawText().ShouldBe("""{"sessionId":"s1","turnId":"msg_2","isAborted":false,"isFailed":false,"changedFiles":["src/berths.ts","/etc/hosts"]}""");
        _host.Requests[1].Request.E.GetRawText().ShouldBe("""{"sessionId":"s1","turnId":"","isAborted":false,"isFailed":false,"changedFiles":[]}""");
    }

    [Fact]
    public async Task A_failed_turn_says_so_and_a_turn_the_user_stopped_says_so()
    {
        _dispatcher.Observe("s1", User, Failed("Provider overloaded"));
        await TurnEnds();
        _turns.Aborted("s1");
        await TurnEnds();

        _host.Requests[0].Request.E.GetRawText().ShouldBe("""{"sessionId":"s1","turnId":"","isAborted":false,"isFailed":true,"failure":"Provider overloaded","changedFiles":[]}""");
        _host.Requests[1].Request.E.GetProperty("isAborted").GetBoolean().ShouldBeTrue();
        _host.Requests[1].Request.E.GetProperty("isFailed").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task A_side_conversation_is_not_a_turn_for_mods()
    {
        Seed("side1", "/work/demo", sideOf: "s1");
        await TurnEnds("side1");

        _host.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_delegated_child_session_is_not_a_turn_for_mods()
    {
        Seed("child1", "/work/demo", parent: "s1");
        await TurnEnds("child1");

        _host.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_follow_up_a_mod_queues_during_the_dispatch_is_only_added_then_sent_once_by_the_queue()
    {
        _dispatcher.Observe("s1", User, Wrote("/work/demo/a.ts"));
        string? refusal = "not asked";
        _host.Reply = async _ =>
        {
            refusal = await _followUps.QueueAsync(_service, "s1", "Run the tests", "tests-after-edits", CancellationToken.None);
            // The session is idle, yet the message waits for the dispatcher's send rather than racing it.
            Sent.ShouldBeEmpty();
            (await _queue.ListAsync("s1")).Select(i => i.Text).ShouldBe(["Run the tests"]);
            return ModDispatchResult.NotDispatched;
        };

        await TurnEnds();

        refusal.ShouldBeNull();
        Sent.ShouldBe(["Run the tests"]);
        (await _queue.ListAsync("s1")).ShouldBeEmpty();
        var queueEvents = _builder.EventBroadcaster.Broadcasts.Where(b => b.Type == PromptQueueService.ChangedEvent).ToList();
        queueEvents.First().Payload.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("text").GetString()).ShouldBe(["Run the tests"]);
    }

    [Fact]
    public async Task A_follow_up_names_the_mod_in_the_queue_and_is_sent_as_that_mods()
    {
        string? shown = null;
        _host.Reply = async _ =>
        {
            await _followUps.QueueAsync(_service, "s1", "Run the tests", "tests-after-edits", CancellationToken.None);
            shown = (await _queue.ListAsync("s1")).Single().Mod;
            return ModDispatchResult.NotDispatched;
        };

        await TurnEnds();

        shown.ShouldBe("tests-after-edits");
        _harness.SendPromptCalls.Single().Options!.SentByMod.ShouldBe("tests-after-edits");
        _builder.EventBroadcaster.Broadcasts.First(b => b.Type == PromptQueueService.ChangedEvent)
            .Payload.GetProperty("items")[0].GetProperty("mod").GetString().ShouldBe("tests-after-edits");
    }

    [Fact]
    public async Task A_follow_up_goes_behind_what_the_user_already_queued()
    {
        await QueueWhileBusyAsync("user's own");
        _host.Reply = async _ =>
        {
            await _followUps.QueueAsync(_service, "s1", "Run the tests", "tests-after-edits", CancellationToken.None);
            return ModDispatchResult.NotDispatched;
        };

        await TurnEnds();

        Sent.ShouldBe(["user's own"], "one message goes per turn, first in first out");
        (await _queue.ListAsync("s1")).Select(i => i.Text).ShouldBe(["Run the tests"]);
    }

    [Fact]
    public async Task A_dispatch_that_takes_more_than_2_seconds_is_left_behind_and_the_queue_goes_on()
    {
        await QueueWhileBusyAsync("waiting");
        var never = new TaskCompletionSource<ModDispatchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _host.Reply = _ => never.Task;

        _dispatcher.Observe("s1", User, Idled());
        await _time.TimerCreated.Within();
        Sent.ShouldBeEmpty("the send waits for the hooks");
        _time.Advance(ModTurnHook.Wait);
        await _dispatcher.Pending.Within();

        Sent.ShouldBe(["waiting"]);
        never.SetException(new InvalidOperationException("late"));
    }

    [Fact]
    public async Task A_host_that_throws_does_not_stop_the_queue()
    {
        await QueueWhileBusyAsync("waiting");
        _host.Reply = _ => throw new InvalidOperationException("pipe closed");

        await TurnEnds();

        Sent.ShouldBe(["waiting"]);
    }

    [Fact]
    public async Task Mods_queue_three_follow_ups_in_a_row_and_the_fourth_waits_for_the_user()
    {
        _builder.ActivityTracker.Update("s1", ActivityStatuses.Busy, User);
        for (var i = 1; i <= 3; i++)
            (await _followUps.QueueAsync(_service, "s1", $"again {i}", "tests-after-edits", CancellationToken.None)).ShouldBeNull();

        var fourth = await _followUps.QueueAsync(_service, "s1", "again 4", "tests-after-edits", CancellationToken.None);

        fourth.ShouldNotBeNull().ShouldContain("3 follow-ups in a row");
        (await _queue.ListAsync("s1")).Select(i => i.Text).ShouldBe(["again 1", "again 2", "again 3"]);

        _followUps.UserSpoke("s1");
        (await _followUps.QueueAsync(_service, "s1", "again 5", "tests-after-edits", CancellationToken.None)).ShouldBeNull();
        (await _followUps.QueueAsync(_service, "other", "elsewhere", "tests-after-edits", CancellationToken.None)).ShouldNotBeNull("no session, so refused");
    }

    [Fact]
    public async Task Empty_or_long_text_and_archived_sessions_are_refused_without_using_up_the_count()
    {
        Seed("old", "/work/demo", retention: "archived");

        (await _followUps.QueueAsync(_service, "s1", "  ", "tests-after-edits", CancellationToken.None)).ShouldNotBeNull();
        (await _followUps.QueueAsync(_service, "s1", new string('x', PromptQueueService.MaxTextLength + 1), null, CancellationToken.None)).ShouldNotBeNull();
        for (var i = 0; i < 5; i++)
            (await _followUps.QueueAsync(_service, "old", "hello", "tests-after-edits", CancellationToken.None)).ShouldNotBeNull().ShouldContain("read-only");

        _builder.ActivityTracker.Update("s1", ActivityStatuses.Busy, User);
        (await _followUps.QueueAsync(_service, "s1", "fine", "tests-after-edits", CancellationToken.None)).ShouldBeNull();
    }

    /// <summary>A clock that says when the 2 s wait started, so a test advances it only then.</summary>
    private sealed class SignalingTime : FakeTimeProvider
    {
        private readonly TaskCompletionSource _created = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task TimerCreated => _created.Task;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            _created.TrySetResult();
            return timer;
        }
    }
}

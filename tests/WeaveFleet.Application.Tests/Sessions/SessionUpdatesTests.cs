using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Application.Tests.Sessions;

public sealed class SessionUpdatesTests
{
    private const string Asker = "s-asker";
    private const string Target = "s-target";
    private const string UserId = "u1";
    private const string MessageId = "msg_from_asker";

    private readonly SessionActivityTracker _activity = new();
    private readonly FakeSender _sender = new();
    private readonly SessionUpdates _sut;

    public SessionUpdatesTests()
    {
        _sut = new SessionUpdates(
            _activity,
            TestServiceScopeFactory.Create(services =>
            {
                services.AddSingleton<IBackgroundUserScope>(new NoUserScope());
                services.AddSingleton<ISessionUpdateSender>(_sender);
            }),
            NullLogger<SessionUpdates>.Instance);
    }

    [Fact]
    public async Task tells_the_asker_when_the_turn_that_handled_its_message_ends()
    {
        _sut.Watch(new SessionUpdateWatch(Asker, Target, UserId, MessageId));

        await ReplyAsync(Target, parentId: MessageId);
        await IdleAsync(Target);

        var sent = _sender.Sent.ShouldHaveSingleItem();
        sent.AskerId.ShouldBe(Asker);
        sent.TargetId.ShouldBe(Target);
        sent.Outcome.ShouldBe(SessionUpdates.Finished);
    }

    [Fact]
    public async Task a_turn_the_target_was_already_running_does_not_count()
    {
        _sut.Watch(new SessionUpdateWatch(Asker, Target, UserId, MessageId));

        // The turn it was on when the message arrived, answering someone else.
        await ReplyAsync(Target, parentId: "msg_from_user");
        await IdleAsync(Target);
        _sender.Read.ShouldBeEmpty();
        _sender.Sent.ShouldBeEmpty();

        await ReplyAsync(Target, parentId: MessageId);
        await IdleAsync(Target);
        _sender.Sent.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task says_so_when_the_turn_fails()
    {
        _sut.Watch(new SessionUpdateWatch(Asker, Target, UserId, MessageId));

        await ReplyAsync(Target, parentId: MessageId);
        _sut.Observe(Target, new TurnFailed
        {
            Payload = new TurnFailedPayload { SessionId = Target, Error = new TurnError { Name = "APIError", Message = "Rate limited." } },
        });
        await IdleAsync(Target);

        _sender.Read.ShouldHaveSingleItem().Failure!.Message.ShouldBe("Rate limited.");
        _sender.Sent.ShouldHaveSingleItem().Outcome.ShouldBe(SessionUpdates.Failed);
    }

    [Fact]
    public async Task says_so_when_the_turn_was_stopped_half_way()
    {
        _sut.Watch(new SessionUpdateWatch(Asker, Target, UserId, MessageId));

        await ReplyAsync(Target, parentId: MessageId);
        _sut.MarkStopped(Target);
        await IdleAsync(Target);

        _sender.Sent.ShouldHaveSingleItem().Outcome.ShouldBe(SessionUpdates.Stopped);
    }

    [Fact]
    public async Task a_stop_before_the_turn_answers_the_message_is_not_that_messages_stop()
    {
        _sut.Watch(new SessionUpdateWatch(Asker, Target, UserId, MessageId));

        // The target's earlier turn is stopped; the watched message hasn't been answered yet.
        _sut.MarkStopped(Target);
        await ReplyAsync(Target, parentId: MessageId);
        await IdleAsync(Target);

        _sender.Sent.ShouldHaveSingleItem().Outcome.ShouldBe(SessionUpdates.Finished);
    }

    [Fact]
    public async Task a_turn_that_ended_before_the_watch_was_registered_is_still_reported()
    {
        await ReplyAsync(Target, parentId: MessageId);
        await IdleAsync(Target);

        _sut.Watch(new SessionUpdateWatch(Asker, Target, UserId, MessageId));
        await _sut.Pending;

        _sender.Sent.ShouldHaveSingleItem().Outcome.ShouldBe(SessionUpdates.Finished);
    }

    [Fact]
    public async Task a_watch_registered_after_the_reply_but_before_the_idle_is_armed_by_it()
    {
        await ReplyAsync(Target, parentId: MessageId);
        _sut.Watch(new SessionUpdateWatch(Asker, Target, UserId, MessageId));
        _sender.Sent.ShouldBeEmpty();

        await IdleAsync(Target);

        _sender.Sent.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task tells_it_once()
    {
        _sut.Watch(new SessionUpdateWatch(Asker, Target, UserId, MessageId));

        await ReplyAsync(Target, parentId: MessageId);
        await IdleAsync(Target);
        await ReplyAsync(Target, parentId: MessageId);
        await IdleAsync(Target);

        _sender.Sent.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task waits_for_the_asker_to_finish_its_own_turn()
    {
        _sut.Watch(new SessionUpdateWatch(Asker, Target, UserId, MessageId));
        _activity.Update(Asker, ActivityStatuses.Busy, UserId);

        await ReplyAsync(Target, parentId: MessageId);
        await IdleAsync(Target);
        _sender.Sent.ShouldBeEmpty();

        _activity.Update(Asker, ActivityStatuses.Idle, UserId);
        await IdleAsync(Asker);
        _sender.Sent.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task an_asker_whose_idle_was_seen_but_not_yet_tracked_is_told_at_once()
    {
        _sut.Watch(new SessionUpdateWatch(Asker, Target, UserId, MessageId));
        _activity.Update(Asker, ActivityStatuses.Busy, UserId);
        await Task.Delay(20);

        // The relay tells SessionUpdates of the asker's idle before the tracker hears of it.
        await IdleAsync(Asker);
        await ReplyAsync(Target, parentId: MessageId);
        await IdleAsync(Target);

        _sender.Sent.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task one_update_per_turn_of_the_asker()
    {
        const string other = "s-other";
        _sut.Watch(new SessionUpdateWatch(Asker, Target, UserId, MessageId));
        _sut.Watch(new SessionUpdateWatch(Asker, other, UserId, "msg_to_other"));
        _activity.Update(Asker, ActivityStatuses.Busy, UserId);

        await ReplyAsync(Target, parentId: MessageId);
        await IdleAsync(Target);
        await ReplyAsync(other, parentId: "msg_to_other");
        await IdleAsync(other);

        await IdleAsync(Asker);
        _sender.Sent.Select(u => u.TargetId).ShouldBe([Target]);

        // The turn the first update started ends; the second is next.
        await IdleAsync(Asker);
        _sender.Sent.Select(u => u.TargetId).ShouldBe([Target, other]);
    }

    [Fact]
    public async Task a_turn_an_update_started_is_marked_until_it_ends()
    {
        _sut.Watch(new SessionUpdateWatch(Asker, Target, UserId, MessageId));
        _sut.IsStartedByUpdate(Asker).ShouldBeFalse();

        await ReplyAsync(Target, parentId: MessageId);
        await IdleAsync(Target);
        _sut.IsStartedByUpdate(Asker).ShouldBeTrue();

        await IdleAsync(Asker);
        _sut.IsStartedByUpdate(Asker).ShouldBeFalse();
    }

    [Fact]
    public async Task a_prompt_of_the_users_own_in_that_turn_ends_the_mark()
    {
        _sut.Watch(new SessionUpdateWatch(Asker, Target, UserId, MessageId));
        await ReplyAsync(Target, parentId: MessageId);
        await IdleAsync(Target);
        _sut.IsStartedByUpdate(Asker).ShouldBeTrue();

        // OpenCode 2 runs a prompt sent mid-turn in the turn it's on, with no idle in between.
        _sut.Prompted(Asker, "And another one.");

        _sut.IsStartedByUpdate(Asker).ShouldBeFalse();
    }

    [Fact]
    public async Task a_message_or_update_from_another_session_keeps_the_mark()
    {
        _sut.Watch(new SessionUpdateWatch(Asker, Target, UserId, MessageId));
        await ReplyAsync(Target, parentId: MessageId);
        await IdleAsync(Target);

        _sut.Prompted(Asker, SessionMessages.Wrap(Target, "Target", "Over to you."));
        _sut.Prompted(Asker, SessionMessages.WrapUpdate(Target, "Target", SessionUpdates.Finished, "Done."));

        _sut.IsStartedByUpdate(Asker).ShouldBeTrue();
    }

    [Fact]
    public async Task an_update_that_was_not_delivered_does_not_mark_the_turn()
    {
        _sender.Deliver = false;
        _sut.Watch(new SessionUpdateWatch(Asker, Target, UserId, MessageId));

        await ReplyAsync(Target, parentId: MessageId);
        await IdleAsync(Target);

        _sender.Sent.ShouldHaveSingleItem();
        _sut.IsStartedByUpdate(Asker).ShouldBeFalse();
    }

    [Fact]
    public async Task an_update_for_an_archived_asker_waits_and_is_sent_when_its_next_turn_ends()
    {
        _sender.Deliver = false;
        _sender.AskerArchived = true;
        _sut.Watch(new SessionUpdateWatch(Asker, Target, UserId, MessageId));

        // The delegated session finishes after its parent was archived: the parent can't be prompted.
        await ReplyAsync(Target, parentId: MessageId);
        await IdleAsync(Target);
        _sender.Sent.ShouldHaveSingleItem();

        // Restored; its next turn ends, and it hears now.
        _sender.Deliver = true;
        _sender.AskerArchived = false;
        await IdleAsync(Asker);

        _sender.Sent.Count.ShouldBe(2);
        _sender.Sent[1].TargetId.ShouldBe(Target);
    }

    [Fact]
    public async Task nobody_hears_about_a_session_they_did_not_ask_about()
    {
        await ReplyAsync(Target, parentId: MessageId);
        await IdleAsync(Target);

        _sender.Read.ShouldBeEmpty();
    }

    [Fact]
    public void the_reply_is_the_last_one_after_the_message()
    {
        HarnessMessage[] page =
        [
            Message("m1", "user", "Earlier."),
            Message("m2", "assistant", "An older answer."),
            Message(MessageId, "user", "The message."),
            Message("m4", "assistant", "Looking."),
            Message("m5", "assistant", "  Docs updated.  "),
            Message("m6", "assistant", ""),
        ];

        SessionUpdateSender.LastReply(page, MessageId).ShouldBe("Docs updated.");
        SessionUpdateSender.LastReply(page[..3], MessageId).ShouldBeNull();
    }

    [Fact]
    public void a_message_is_answered_by_its_own_turn_not_a_later_one()
    {
        HarnessMessage[] page =
        [
            Message(MessageId, "user", "First."),
            Message("m2", "assistant", "Answer to first."),
            Message("m3", "user", "Second."),
            Message("m4", "assistant", "Answer to second."),
        ];

        SessionUpdateSender.LastReply(page, MessageId).ShouldBe("Answer to first.");
        SessionUpdateSender.LastReply(page, "m3").ShouldBe("Answer to second.");
    }

    [Fact]
    public void a_message_answered_only_by_a_later_turn_has_no_reply_of_its_own()
    {
        HarnessMessage[] page =
        [
            Message(MessageId, "user", "First."),
            Message("m3", "user", "Second."),
            Message("m4", "assistant", "Answer to both."),
        ];

        SessionUpdateSender.LastReply(page, MessageId).ShouldBeNull();
        SessionUpdateSender.IsSuperseded(page, MessageId).ShouldBeTrue();
        SessionUpdateSender.IsSuperseded(page, "m3").ShouldBeFalse();
    }

    [Fact]
    public void a_long_reply_is_cut_at_a_word()
    {
        var reply = string.Join(' ', Enumerable.Repeat("word", 400));

        var shortened = SessionUpdateSender.Shorten(reply);

        shortened.Length.ShouldBeLessThanOrEqualTo(SessionUpdateSender.MaxReplyLength + 1);
        shortened.ShouldEndWith("word…");
        SessionUpdateSender.Shorten("Short.").ShouldBe("Short.");
    }

    private async Task ReplyAsync(string sessionId, string parentId)
    {
        _sut.Observe(sessionId, new MessageUpdated
        {
            Payload = new MessageLifecyclePayload
            {
                Info = new MessageEventInfo
                {
                    Id = $"reply_{Guid.NewGuid():N}",
                    Role = "assistant",
                    SessionId = sessionId,
                    ParentId = parentId,
                    Time = new MessageEventTime { Created = 0 },
                },
            },
        });
        await _sut.Pending;
    }

    private async Task IdleAsync(string sessionId)
    {
        _sut.Observe(sessionId, new SessionIdled { Payload = new SessionIdledPayload { SessionId = sessionId } });
        await _sut.Pending;
    }

    private static HarnessMessage Message(string id, string role, string text) => new()
    {
        Id = id,
        Role = role,
        Parts = [new TextPart(text)],
        Timestamp = DateTimeOffset.UnixEpoch,
    };

    private sealed class FakeSender : ISessionUpdateSender
    {
        public List<(SessionUpdateWatch Watch, TurnError? Failure)> Read { get; } = [];
        public List<SessionUpdate> Sent { get; } = [];
        public bool Deliver { get; set; } = true;
        public bool AskerArchived { get; set; }

        public Task<bool> AskerArchivedAsync(SessionUpdate update, CancellationToken ct) => Task.FromResult(AskerArchived);

        public Task<SessionUpdate?> ReadAsync(SessionUpdateWatch watch, TurnError? failure, CancellationToken ct)
        {
            Read.Add((watch, failure));
            var outcome = SessionUpdates.OutcomeOf(failure);
            return Task.FromResult<SessionUpdate?>(new SessionUpdate(watch.AskerId, watch.TargetId, watch.UserId, "…", outcome));
        }

        public Task<bool> SendAsync(SessionUpdate update, CancellationToken ct)
        {
            Sent.Add(update);
            return Task.FromResult(Deliver);
        }
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

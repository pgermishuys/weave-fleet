using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Data.Repositories;
using WeaveFleet.Infrastructure.Events;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;
using WeaveFleet.Infrastructure.Tests.Data;
using WeaveFleet.Infrastructure.Tests.Data.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Events;

/// <summary>
/// Every harness shows where it compacted the conversation the same way: a <see cref="CompactionPart"/> divider, live and
/// after a reload, with the summary behind it. OpenCode's own summary message is marked so it shows once, behind its
/// divider, not again as a reply.
/// </summary>
public sealed class CompactionDividerTests
{
    private const string FleetSession = "fleet-1";

    // ── OpenCode 2 ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OpenCode2_history_shows_a_finished_compaction_as_a_divider_with_its_summary()
    {
        // V2's Session.Message.Compaction.Completed (packages/schema/src/session-message.ts).
        var messages = OpenCode2History.ToHarnessMessages(
        [
            new OpenCode2Message { Id = "msg_1", Type = "compaction", Status = "completed", Reason = "auto", Summary = "## Goal\nShip it.", Time = new OpenCode2MessageTimes { Created = 1 } },
            new OpenCode2Message { Id = "msg_2", Type = "compaction", Status = "running", Reason = "manual", Summary = "", Time = new OpenCode2MessageTimes { Created = 2 } },
        ]);

        var divider = messages.ShouldHaveSingleItem().Parts.ShouldHaveSingleItem().ShouldBeOfType<CompactionPart>();
        divider.Trigger.ShouldBe(ContextCompactionTriggers.Auto);
        divider.Summary.ShouldBe("## Goal\nShip it.");
        divider.PartId.ShouldBe("msg_1-compaction");
    }

    [Fact]
    public void OpenCode2_live_shows_a_divider_when_a_compaction_ends()
    {
        var mapper = new OpenCode2Mapper(FleetSession);
        var events = mapper.Map(new OpenCode2Event
        {
            Id = "evt_9",
            Created = 1_789_763_753_000,
            Type = "session.compaction.ended",
            Data = JsonDocument.Parse("""{"sessionID":"ses_1","reason":"manual","text":"The summary.","recent":""}""").RootElement.Clone(),
        });

        events.Select(e => e.Type).ShouldBe([EventTypes.MessageUpdated, EventTypes.MessagePartUpdated]);
        var translator = new DomainEventTranslator(NullLogger<DomainEventTranslator>.Instance);
        var part = translator.Translate(events[1] with { FleetSessionId = FleetSession }).ShouldBeOfType<MessagePartUpdated>()
            .Payload.Part.ShouldBeOfType<CompactionMessageEventPart>();
        part.Trigger.ShouldBe(ContextCompactionTriggers.Manual);
        part.Summary.ShouldBe("The summary.");
        part.MessageId.ShouldBe("evt_9");
    }

    // ── OpenCode ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OpenCode_history_shows_its_compaction_part_as_a_divider_and_marks_its_summary_message()
    {
        var compaction = OpenCodeMapper.ToHarnessMessage(new OpenCodeMessageWithParts
        {
            Info = new OpenCodeUserMessage { Id = "msg_1", SessionId = "ses_1", Time = new OpenCodeMessageTime { Created = 1 } },
            Parts = [new OpenCodeCompactionPart { Id = "prt_1", Auto = true }],
        });
        var summary = OpenCodeMapper.ToHarnessMessage(new OpenCodeMessageWithParts
        {
            Info = new OpenCodeAssistantMessage { Id = "msg_2", SessionId = "ses_1", Time = new OpenCodeMessageTime { Created = 2 }, Mode = "compaction", Summary = JsonDocument.Parse("true").RootElement },
            Parts = [new OpenCodeTextPart { Id = "prt_2", Text = "What we did so far." }],
        });

        var divider = compaction.Parts.ShouldHaveSingleItem().ShouldBeOfType<CompactionPart>();
        divider.Trigger.ShouldBe(ContextCompactionTriggers.Auto);
        divider.PartId.ShouldBe("prt_1");
        summary.CompactionSummary.ShouldBeTrue();
        compaction.CompactionSummary.ShouldBeFalse();
    }

    [Fact]
    public void OpenCode_live_marks_the_summary_message_and_shows_the_compaction_part()
    {
        var translator = new DomainEventTranslator(NullLogger<DomainEventTranslator>.Instance);

        var summary = translator.Translate(Event(EventTypes.MessageUpdated,
            """{"info":{"id":"msg_2","role":"assistant","sessionID":"ses_1","time":{"created":2},"mode":"compaction","summary":true}}"""))
            .ShouldBeOfType<MessageUpdated>();
        // A user message's own "summary" is an object (its diffs): it isn't a compaction's.
        var prompt = translator.Translate(Event(EventTypes.MessageUpdated,
            """{"info":{"id":"msg_3","role":"user","sessionID":"ses_1","time":{"created":3},"summary":{"diffs":[]}}}"""))
            .ShouldBeOfType<MessageUpdated>();
        var part = translator.Translate(Event(EventTypes.MessagePartUpdated,
            """{"sessionID":"ses_1","part":{"type":"compaction","id":"prt_1","sessionID":"ses_1","messageID":"msg_1","auto":false}}"""))
            .ShouldBeOfType<MessagePartUpdated>();

        summary.Payload.Info.CompactionSummary.ShouldBe(true);
        prompt.Payload.Info.CompactionSummary.ShouldBeNull();
        part.Payload.Part.ShouldBeOfType<CompactionMessageEventPart>().Id.ShouldBe("prt_1");
    }

    // ── Fleet's own history (Claude Code, Pi) ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_saved_divider_and_a_saved_turn_failure_come_back_in_the_snapshot()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        using var _ = keeper;
        var session = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, TestUserContext.DefaultUserId);
        var messages = new MessageRepository(factory, new TestUserContext());
        var divider = new HarnessMessage
        {
            Id = "msg_1",
            Role = "assistant",
            Timestamp = DateTimeOffset.UtcNow,
            Parts = [new CompactionPart(ContextCompactionTriggers.Manual, 35_438, 1_450, "The summary.") { PartId = "msg_1-part-0" }],
        };
        var failure = MessagePersistenceService.CreateTurnFailureMessage(
            new TurnError { Name = "APIError", Message = "Overloaded" }, DateTimeOffset.UtcNow) with { Id = "msg_2" };
        await messages.UpsertAsync(MessagePersistenceService.ToPersistedMessage(session.Session.Id, divider));
        await messages.UpsertAsync(MessagePersistenceService.ToPersistedMessage(session.Session.Id, failure));

        var snapshot = await new SessionSnapshotBuilder(factory, new TestUserContext(), new SessionActivityTracker()).BuildAsync(session.Session.Id);

        var part = snapshot.Messages.Single(m => m.Info.Id == "msg_1").Parts.ShouldHaveSingleItem().ShouldBeOfType<CompactionMessageEventPart>();
        part.Id.ShouldBe("msg_1-part-0");
        part.TokensBefore.ShouldBe(35_438);
        part.TokensAfter.ShouldBe(1_450);
        part.Summary.ShouldBe("The summary.");
        // Fleet's saved failures (TurnFailureRecorder) come back too, so a failure card survives a reload.
        snapshot.Messages.Single(m => m.Info.Id == "msg_2").Info.Error.ShouldNotBeNull().Message.ShouldBe("Overloaded");
    }

    private static HarnessEvent Event(string type, string payload) => new()
    {
        Type = type,
        SessionId = "ses_1",
        FleetSessionId = FleetSession,
        Timestamp = DateTimeOffset.UtcNow,
        Payload = JsonDocument.Parse(payload).RootElement.Clone(),
    };
}

using System.Text.Json;
using Shouldly;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Events;

namespace WeaveFleet.Application.Tests.Sessions;

public sealed class SessionNowTrackerTests
{
    private static MessagePartUpdated Part(MessageEventPart part) =>
        new() { Payload = new MessagePartUpdatedPayload { SessionId = "s1", Part = part } };

    private static ToolMessageEventPart Tool(string name, string input, bool completed = false) => new()
    {
        Id = "p", SessionId = "s1", MessageId = "m", ToolName = name, CallId = "c",
        State = completed
            ? new ToolCompletedState { Input = JsonDocument.Parse(input).RootElement }
            : new ToolRunningState { Input = JsonDocument.Parse(input).RootElement },
    };

    [Fact]
    public void The_latest_tool_call_is_the_line_and_a_thought_or_reply_replaces_it()
    {
        var tracker = new SessionNowTracker();

        tracker.Observe("s1", "u1", Part(Tool("bash", """{"command":"pnpm test"}""")));
        tracker.GetFor("u1")["s1"].ShouldBe(tracker.GetFor("u1")["s1"] with { Text = "bash · pnpm test", Kind = "tool" });

        tracker.Observe("s1", "u1", Part(new ReasoningMessageEventPart { Id = "r", SessionId = "s1", MessageId = "m", Text = "hmm" }));
        (tracker.GetFor("u1")["s1"].Text, tracker.GetFor("u1")["s1"].Kind).ShouldBe(("Thinking", "thinking"));

        tracker.Observe("s1", "u1", Part(new TextMessageEventPart { Id = "t", SessionId = "s1", MessageId = "m", Text = "Done.\nMore." }));
        (tracker.GetFor("u1")["s1"].Text, tracker.GetFor("u1")["s1"].Kind).ShouldBe(("Done.", "reply"));
    }

    [Fact]
    public void Events_that_say_nothing_about_what_the_agent_does_are_ignored()
    {
        var tracker = new SessionNowTracker();

        tracker.Observe("s1", "u1", null);
        tracker.Observe("s1", "u1", Part(new TextMessageEventPart { Id = "t", SessionId = "s1", MessageId = "m", Text = "" }));
        tracker.Observe("s1", "u1", Part(new StepStartedMessageEventPart { Id = "s", SessionId = "s1", MessageId = "m", Index = 0 }));

        tracker.GetFor("u1").ShouldBeEmpty();
    }

    [Fact]
    public void A_user_sees_only_their_own_sessions_and_a_forgotten_session_is_gone()
    {
        var tracker = new SessionNowTracker();
        tracker.Observe("s1", "u1", Part(Tool("read", """{"path":"a.md"}""")));
        tracker.Observe("s2", "u2", Part(Tool("read", """{"path":"b.md"}""")));

        tracker.GetFor("u1").Keys.ShouldBe(["s1"]);

        tracker.Forget("s1");
        tracker.GetFor("u1").ShouldBeEmpty();
    }

    [Fact]
    public void It_is_inactive_until_asked_to_start()
    {
        var tracker = new SessionNowTracker();
        tracker.IsActive.ShouldBeFalse();

        tracker.Activate();

        tracker.IsActive.ShouldBeTrue();
    }
}

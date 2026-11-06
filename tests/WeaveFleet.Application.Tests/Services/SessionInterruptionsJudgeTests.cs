using System.Text.Json;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Application.Tests.Services;

/// <summary>How the startup check reads a flagged session's history: was its last turn finished, or cut off?</summary>
public sealed class SessionInterruptionsJudgeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 17, 14, 0, 0, TimeSpan.Zero);

    private static HarnessMessage Message(string role, int minute, string? finish = null, TurnError? error = null, params MessagePart[] parts) => new()
    {
        Id = $"m{minute}",
        Role = role,
        Parts = parts,
        Timestamp = T0.AddMinutes(minute),
        Finish = finish,
        Error = error,
    };

    private static ToolUsePart Tool(string name, ToolUseState state) => new("call-1", name, JsonDocument.Parse("{}").RootElement, state);

    [Fact]
    public void An_assistant_message_that_finished_means_the_turn_was_over()
    {
        var (finished, call) = SessionInterruptions.Judge([Message("user", 0), Message("assistant", 1, finish: "stop", parts: new TextPart("Done."))]);

        finished.ShouldBeTrue();
        call.ShouldBeNull();
    }

    [Fact]
    public void A_tool_call_still_running_means_the_turn_was_cut_off_and_names_the_call()
    {
        var (finished, call) = SessionInterruptions.Judge([Message("user", 0), Message("assistant", 1, parts: Tool("bash", ToolUseState.Running))]);

        finished.ShouldBeFalse();
        call.ShouldBe("bash");
    }

    [Fact]
    public void A_last_message_from_the_user_or_no_messages_means_the_turn_was_cut_off()
    {
        SessionInterruptions.Judge([Message("user", 0)]).Finished.ShouldBeFalse();
        SessionInterruptions.Judge([]).Finished.ShouldBeFalse();
    }

    [Fact]
    public void An_assistant_message_with_no_finish_reason_was_cut_off()
    {
        SessionInterruptions.Judge([Message("user", 0), Message("assistant", 1, parts: new TextPart("Working on"))]).Finished.ShouldBeFalse();
    }

    [Fact]
    public void The_newest_message_decides_whatever_order_they_arrive_in()
    {
        var older = Message("assistant", 1, finish: "stop", parts: new TextPart("Done."));
        var newer = Message("user", 2);

        SessionInterruptions.Judge([newer, older]).Finished.ShouldBeFalse();
    }

    [Fact]
    public void A_call_history_shows_as_cut_off_names_the_call_and_its_command()
    {
        var cutOff = new ToolUsePart("call-1", "bash", JsonDocument.Parse("""{"command":"bun test\nmore"}""").RootElement, ToolUseState.Error)
        {
            Error = CutOffCall.Message,
        };

        var (finished, call) = SessionInterruptions.Judge([Message("user", 0), Message("assistant", 1, parts: cutOff)]);

        finished.ShouldBeFalse();
        call.ShouldBe("bash (bun test)");
    }

    [Fact]
    public void A_call_that_failed_for_its_own_reason_is_not_the_cut_off_call()
    {
        var failed = Tool("bash", ToolUseState.Error) with { Error = "exit 1" };

        SessionInterruptions.Judge([Message("user", 0), Message("assistant", 1, finish: "stop", parts: failed)]).LastCall.ShouldBeNull();
    }

    [Theory]
    [InlineData("tool-calls")]
    [InlineData("tool_use")]
    public void A_message_that_ended_on_its_tool_calls_was_cut_off_and_names_the_last_call(string finish)
    {
        // OpenCode 2 ends a running call with "Killed by SIGTERM" when Fleet stops it: the call completes, the model never answers.
        var killed = new ToolUsePart("call-1", "shell", JsonDocument.Parse("""{"command":"echo start && sleep 600"}""").RootElement, ToolUseState.Completed);

        var (finished, call) = SessionInterruptions.Judge([Message("user", 0), Message("assistant", 1, finish: finish, parts: killed)]);

        finished.ShouldBeFalse();
        call.ShouldBe("shell (echo start && sleep 600)");
    }
}

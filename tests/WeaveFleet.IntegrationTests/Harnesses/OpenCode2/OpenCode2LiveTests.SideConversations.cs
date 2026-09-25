extern alias FakeLlm;

using FakeLlm::FakeLlmServer;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.IntegrationTests.Harnesses.OpenCode2;

/// <summary>
/// A side question (<c>/btw</c>) on a real V2: asked while the session's turn waits on a question, it goes to a fork
/// (<c>session.fork</c> with <c>before</c>) made at the last finished turn, with the boundary note added as a synthetic
/// message that waits for the prompt. The session's turn and history are left alone, and closing it deletes the fork.
/// This fails if a future V2 changes what <c>before</c> copies or starts a turn on a note sent with <c>resume: false</c>.
/// </summary>
public sealed partial class OpenCode2LiveTests
{
    [OpenCode2Fact]
    public async Task A_side_question_forks_the_session_at_its_last_finished_turn_and_leaves_the_session_alone()
    {
        const string first = "What does this repo do? (btw: first)";
        const string running = "Which one should I pick? Ask me. (btw: running)";
        const string side = "What did I ask first? (btw: side)";
        fleet.Answer(request => LlmRequest.Starts(request, first) ? new ScriptedLlmResponse { Text = "It parses things." }
            : LlmRequest.Starts(request, running) ? ToolCall("call_btw_pick", "question", Question("Pick one"))
            : LlmRequest.Starts(request, side) ? new ScriptedLlmResponse { Text = "You asked what this repo does." }
            : null);

        using var cts = new CancellationTokenSource(Timeout);
        var id = await fleet.CreateSessionAsync(fleet.NewFolder("btw"), "Side question", cts.Token);
        var events = fleet.Watch(cts.Token, id);
        await PromptAsync(id, first, options: null, cts.Token);
        await WaitForAsync(events, () => events.For(id).Any(e => e.Type == "session.idle"), cts.Token);

        // A turn that is still running: it waits on the user's answer.
        await PromptAsync(id, running, options: null, cts.Token);
        var harness = await fleet.HarnessSessionAsync(id, cts.Token);
        await WaitForAsync(events, async () => await harness.GetActivityStatusAsync(cts.Token) == ActivityStatuses.WaitingInput, cts.Token);
        var history = (await harness.GetMessagesAsync(null, cts.Token)).Messages.Select(m => m.Id).ToList();

        var asked = await fleet.WithOrchestratorAsync(o => o.AskSideQuestionAsync(id, side, null, null, cts.Token));
        asked.IsSuccess.ShouldBeTrue(asked.IsFailure ? asked.Error.Description : null);
        var sideConversation = asked.Value.SideConversation;

        // The model got the finished turn and the boundary, and not the running turn.
        var request = (string)await WaitForAsync(events, () => Task.FromResult<object?>(fleet.Llm.Queue.Requests.FirstOrDefault(r => LlmRequest.Starts(r, side))), cts.Token);
        var texts = LlmRequest.UserTexts(request);
        texts.ShouldContain(t => t.Contains(first, StringComparison.Ordinal));
        texts.ShouldContain(t => t.Contains(SideConversations.BoundaryInstruction, StringComparison.Ordinal));
        texts.ShouldNotContain(t => t.Contains(running, StringComparison.Ordinal));

        // Its own conversation, after the boundary: the question and the answer; the note isn't shown.
        var sideHarness = await fleet.HarnessSessionAsync(sideConversation.Id, cts.Token);
        var own = (List<HarnessMessage>)await WaitForAsync(events, async () =>
        {
            var messages = (await sideHarness.GetMessagesAsync(null, cts.Token)).Messages;
            var after = messages.SkipWhile(m => m.Id != sideConversation.SideBoundaryMessageId).Skip(1).ToList();
            return after.Any(m => m.Role == "assistant" && m.Parts.OfType<TextPart>().Any(p => p.Text.Contains("You asked", StringComparison.Ordinal))) ? after : null;
        }, cts.Token);
        own.Select(m => m.Role).ShouldBe(["user", "assistant"]);
        own[0].Parts.OfType<TextPart>().Select(p => p.Text).ShouldBe([side]);

        // The session still waits on its question, with the history it had.
        (await harness.GetActivityStatusAsync(cts.Token)).ShouldBe(ActivityStatuses.WaitingInput);
        (await harness.GetMessagesAsync(null, cts.Token)).Messages.Select(m => m.Id).ShouldBe(history);

        (await fleet.WithOrchestratorAsync(o => o.CloseSideConversationAsync(id, cts.Token))).IsSuccess.ShouldBeTrue();
        (await fleet.WithOrchestratorAsync(o => o.GetSideConversationAsync(id))).Value.ShouldBeNull();
        // V2 no longer has the fork: reading it fails.
        await Should.ThrowAsync<Exception>(() => sideHarness.GetMessagesAsync(null, cts.Token));

        (await fleet.WithOrchestratorAsync(o => o.AnswerQuestionAsync(id, "call_btw_pick", [["B"]], cts.Token))).IsSuccess.ShouldBeTrue();
    }
}

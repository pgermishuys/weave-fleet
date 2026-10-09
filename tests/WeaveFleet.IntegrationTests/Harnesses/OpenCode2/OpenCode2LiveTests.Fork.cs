extern alias FakeLlm;

using FakeLlm::FakeLlmServer;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;

namespace WeaveFleet.IntegrationTests.Harnesses.OpenCode2;

/// <summary>
/// Fork on a real V2: the new session holds a copy of the conversation up to the last finished turn (a turn still
/// waiting on the user isn't copied), the model reads that copy on the fork's first prompt, and from there the two
/// conversations go their own ways. The fork outlives its parent. This fails if a future V2 changes what
/// <c>session.fork</c> copies, or deletes a fork with the session it was made from.
/// </summary>
public sealed partial class OpenCode2LiveTests
{
    [OpenCode2Fact]
    public async Task A_fork_copies_the_conversation_and_then_goes_its_own_way()
    {
        const string first = "What does this repo do? (fork: first)";
        const string running = "Which one should I pick? Ask me. (fork: running)";
        const string forkOnly = "Only the fork hears this. (fork: fork)";
        const string parentOnly = "Only the parent hears this. (fork: parent)";
        fleet.Answer(request => LlmRequest.Starts(request, first) ? new ScriptedLlmResponse { Text = "It parses things." }
            : LlmRequest.Starts(request, running) ? ToolCall("call_fork_pick", "question", Question("Pick one"))
            : LlmRequest.Starts(request, forkOnly) ? new ScriptedLlmResponse { Text = "Fork reply." }
            : LlmRequest.Starts(request, parentOnly) ? new ScriptedLlmResponse { Text = "Parent reply." }
            : null);

        using var cts = new CancellationTokenSource(Timeout);
        var id = await fleet.CreateSessionAsync(fleet.NewFolder("fork"), "Fork me", cts.Token);
        var events = fleet.Watch(cts.Token, id);
        await PromptAsync(id, first, options: null, cts.Token);
        await WaitForAsync(events, () => events.For(id).Any(e => e.Type == "session.idle"), cts.Token);

        // A turn that is still running: it waits on the user's answer, and isn't copied.
        await PromptAsync(id, running, options: null, cts.Token);
        var parent = await fleet.HarnessSessionAsync(id, cts.Token);
        await WaitForAsync(events, async () => await parent.GetActivityStatusAsync(cts.Token) == ActivityStatuses.WaitingInput, cts.Token);

        var forked = await fleet.WithOrchestratorAsync(o => o.ForkSessionAsync(id, null, cts.Token));
        forked.IsSuccess.ShouldBeTrue(forked.IsFailure ? forked.Error.Description : null);
        var forkId = forked.Value.Session.Id;
        forked.Value.Session.Title.ShouldBe("Fork of Fork me");
        forked.Value.Session.HarnessType.ShouldBe(OpenCode2HarnessSession.Type);

        // The copy: the finished turn, and nothing of the one still running.
        var fork = await fleet.HarnessSessionAsync(forkId, cts.Token);
        var copied = (await fork.GetMessagesAsync(null, cts.Token)).Messages;
        MessageUserTexts(copied).ShouldBe([first]);
        copied.ShouldContain(m => m.Role == "assistant" && m.Parts.OfType<TextPart>().Any(p => p.Text == "It parses things."));

        // The parent still waits on its question; forking didn't touch it.
        (await parent.GetActivityStatusAsync(cts.Token)).ShouldBe(ActivityStatuses.WaitingInput);
        (await fleet.WithOrchestratorAsync(o => o.AnswerQuestionAsync(id, "call_fork_pick", [["B"]], cts.Token))).IsSuccess.ShouldBeTrue();

        // The fork's first prompt reads the copied conversation.
        var forkEvents = fleet.Watch(cts.Token, forkId);
        await PromptAsync(forkId, forkOnly, options: null, cts.Token);
        var forkRequest = (string)await WaitForAsync(forkEvents, () => Task.FromResult<object?>(fleet.Llm.Queue.Requests.FirstOrDefault(r => LlmRequest.Starts(r, forkOnly))), cts.Token);
        LlmRequest.UserTexts(forkRequest).ShouldContain(t => t.Contains(first, StringComparison.Ordinal));
        LlmRequest.UserTexts(forkRequest).ShouldNotContain(t => t.Contains(running, StringComparison.Ordinal));
        await WaitForAsync(forkEvents, () => forkEvents.For(forkId).Any(e => e.Type == "session.idle"), cts.Token);

        await WaitForAsync(events, async () => !SessionActivityTracker.IsInTurn(await parent.GetActivityStatusAsync(cts.Token)), cts.Token);
        await PromptAsync(id, parentOnly, options: null, cts.Token);
        var parentRequest = (string)await WaitForAsync(events, () => Task.FromResult<object?>(fleet.Llm.Queue.Requests.FirstOrDefault(r => LlmRequest.Starts(r, parentOnly))), cts.Token);
        LlmRequest.UserTexts(parentRequest).ShouldNotContain(t => t.Contains(forkOnly, StringComparison.Ordinal));

        // Each conversation has its own messages and not the other's.
        var parentTexts = await WaitForAsync(events, async () =>
        {
            var texts = MessageUserTexts((await parent.GetMessagesAsync(null, cts.Token)).Messages);
            return texts.Contains(parentOnly) ? texts : null;
        }, cts.Token);
        parentTexts.ShouldNotContain(forkOnly);
        MessageUserTexts((await fork.GetMessagesAsync(null, cts.Token)).Messages).ShouldBe([first, forkOnly]);

        // The fork outlives the session it came from.
        await WaitForAsync(events, async () => !SessionActivityTracker.IsInTurn(await parent.GetActivityStatusAsync(cts.Token)), cts.Token);
        (await fleet.WithOrchestratorAsync(o => o.DeleteSessionAsync(id, cts.Token))).IsSuccess.ShouldBeTrue();
        var afterDelete = await fleet.HarnessSessionAsync(forkId, cts.Token);
        MessageUserTexts((await afterDelete.GetMessagesAsync(null, cts.Token)).Messages).ShouldBe([first, forkOnly]);
    }

    private static List<string> MessageUserTexts(IReadOnlyList<HarnessMessage> messages)
        => [.. messages.Where(m => m.Role == "user").Select(m => string.Concat(m.Parts.OfType<TextPart>().Select(p => p.Text)))];
}

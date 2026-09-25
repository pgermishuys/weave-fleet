using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.IntegrationTests.Harnesses.OpenCode;

/// <summary>
/// A side question (<c>/btw</c>) on a real pooled <c>opencode</c>: asked while the session's turn waits on a question,
/// it goes to a fork made at the last finished turn, on the same process, with the boundary note as a synthetic part;
/// the session's turn and history are left alone, and closing the side conversation deletes the fork. This fails if a
/// future OpenCode changes what <c>fork</c> copies or how it reads <c>messageID</c>.
/// </summary>
public sealed partial class WorkflowStepToolLiveTests
{
    [OpenCodeFact]
    public async Task A_side_question_forks_the_session_at_its_last_finished_turn_and_leaves_the_session_alone()
    {
        await RunAsync(workflowsOn: false, stepPrompt: StepPrompt, async (services, llm, normal, _, ct) =>
        {
            var activity = services.GetRequiredService<SessionActivityTracker>();
            await normal.SendPromptAsync(NormalPrompt, null, ct);
            await WaitForAsync(() => Turns(llm), r => r.Any(t => FirstUserText(t) == NormalPrompt), ct);
            await WaitForAsync(() => activity.Get(Normal)?.ActivityStatus, status => status is not null && !SessionActivityTracker.IsInTurn(status), ct);

            // A turn that is still running: it waits on the user's answer.
            await normal.SendPromptAsync(PickPrompt, null, ct);
            await WaitForAsync(() => activity.Get(Normal)?.ActivityStatus, status => status == ActivityStatuses.WaitingInput, ct);
            var history = (await normal.GetMessagesAsync(null, ct)).Messages.Select(m => m.Id).ToList();

            var asked = await WithOrchestratorAsync(services, o => o.AskSideQuestionAsync(Normal, SidePrompt, null, null, ct));
            asked.IsSuccess.ShouldBeTrue(asked.IsFailure ? asked.Error.Description : null);
            var side = asked.Value.SideConversation;

            // The model got the finished turn and the boundary, not the running turn, and the parent's tools (so the
            // copied conversation is read from the provider's cache).
            var sideTurn = await WaitForAsync(() => Turns(llm).FirstOrDefault(t => LastUserText(t)?.Contains(SidePrompt, StringComparison.Ordinal) == true), t => t is not null, ct);
            UserTexts(sideTurn!).ShouldContain(NormalPrompt);
            UserTexts(sideTurn!).ShouldNotContain(t => t.Contains(PickPrompt, StringComparison.Ordinal));
            LastUserText(sideTurn!)!.ShouldContain(SideConversations.BoundaryInstruction);
            OfferedToolNames([sideTurn!]).ShouldBe(OfferedToolNames(Turns(llm).Where(t => FirstUserText(t) == NormalPrompt).Take(1)), ignoreOrder: true);

            // Its own conversation, after the boundary: the question as asked (no note) and the answer.
            var sideHarness = services.GetRequiredService<InstanceTracker>().Get(side.InstanceId).ShouldNotBeNull();
            var sideMessages = await WaitForAsync(
                async () => (await sideHarness.GetMessagesAsync(null, ct)).Messages,
                messages => messages.Any(m => m.Role == "assistant" && m.Parts.OfType<TextPart>().Any(p => p.Text == SideAnswer)),
                ct);
            var own = sideMessages.SkipWhile(m => m.Id != side.SideBoundaryMessageId).Skip(1).ToList();
            own.Select(m => m.Role).ShouldBe(["user", "assistant"]);
            own[0].Parts.OfType<TextPart>().Select(p => p.Text).ShouldBe([SidePrompt]);

            // The session still waits on its question, with the history it had.
            activity.Get(Normal)!.ActivityStatus.ShouldBe(ActivityStatuses.WaitingInput);
            (await normal.GetMessagesAsync(null, ct)).Messages.Select(m => m.Id).ShouldBe(history);

            // Closing deletes the fork from OpenCode.
            var opencode = (OpenCodeHarnessSession)normal;
            (await opencode.ListFolderSessionsAsync(ct)).Select(s => s.Id).ShouldContain(side.OpencodeSessionId);
            (await WithOrchestratorAsync(services, o => o.CloseSideConversationAsync(Normal, ct))).IsSuccess.ShouldBeTrue();
            (await opencode.ListFolderSessionsAsync(ct)).Select(s => s.Id).ShouldNotContain(side.OpencodeSessionId);
            (await WithOrchestratorAsync(services, o => o.GetSideConversationAsync(Normal))).Value.ShouldBeNull();

            await normal.AbortAsync(ct);
        });
    }

    private static async Task<T> WithOrchestratorAsync<T>(IServiceProvider services, Func<SessionOrchestrator, Task<T>> call)
    {
        using var user = BackgroundUserContext.BeginScope(Owner);
        using var scope = services.CreateScope();
        return await call(scope.ServiceProvider.GetRequiredService<SessionOrchestrator>());
    }
}

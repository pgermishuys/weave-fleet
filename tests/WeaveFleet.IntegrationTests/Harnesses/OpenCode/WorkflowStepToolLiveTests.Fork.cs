using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.IntegrationTests.Harnesses.OpenCode;

/// <summary>
/// Fork on a real pooled <c>opencode</c>: the new session holds a copy of the conversation up to the last finished turn,
/// on the same process, the model reads that copy on the fork's first prompt, and from there the two conversations go
/// their own ways. This fails if a future OpenCode changes what <c>fork</c> copies.
/// </summary>
public sealed partial class WorkflowStepToolLiveTests
{
    private const string ForkPrompt = "FORK: only the fork hears this.";
    private const string ParentPrompt = "PARENT: only the parent hears this.";

    [OpenCodeFact]
    public async Task A_fork_copies_the_conversation_and_then_goes_its_own_way()
    {
        await RunAsync(workflowsOn: false, stepPrompt: StepPrompt, async (services, llm, normal, _, ct) =>
        {
            var activity = services.GetRequiredService<SessionActivityTracker>();
            await normal.SendPromptAsync(NormalPrompt, null, ct);
            await WaitForAsync(() => Turns(llm), r => r.Any(t => FirstUserText(t) == NormalPrompt), ct);
            await WaitForAsync(() => activity.Get(Normal)?.ActivityStatus, status => status is not null && !SessionActivityTracker.IsInTurn(status), ct);
            var parentHistory = await WaitForAsync(
                async () => (await normal.GetMessagesAsync(null, ct)).Messages,
                messages => messages.Any(m => m.Role == "assistant" && m.Parts.OfType<TextPart>().Any()),
                ct);

            var forked = await WithOrchestratorAsync(services, o => o.ForkSessionAsync(Normal, null, ct));
            forked.IsSuccess.ShouldBeTrue(forked.IsFailure ? forked.Error.Description : null);
            var fork = services.GetRequiredService<InstanceTracker>().Get(forked.Value.InstanceId).ShouldNotBeNull();

            // The copy has the parent's conversation, under ids of its own.
            var copied = (await fork.GetMessagesAsync(null, ct)).Messages;
            copied.Select(m => m.Role).ShouldBe(parentHistory.Select(m => m.Role));
            MessageUserTexts(copied).ShouldBe([NormalPrompt]);

            // The fork's first prompt reads it.
            await fork.SendPromptAsync(ForkPrompt, null, ct);
            var forkTurn = await WaitForAsync(() => Turns(llm).FirstOrDefault(t => LastUserText(t) == ForkPrompt), t => t is not null, ct);
            UserTexts(forkTurn!).ShouldContain(NormalPrompt);

            // The parent's next turn doesn't see the fork's, and the other way around.
            await WaitForAsync(() => activity.Get(forked.Value.Session.Id)?.ActivityStatus, status => status is not null && !SessionActivityTracker.IsInTurn(status), ct);
            await normal.SendPromptAsync(ParentPrompt, null, ct);
            var parentTurn = await WaitForAsync(() => Turns(llm).FirstOrDefault(t => LastUserText(t) == ParentPrompt), t => t is not null, ct);
            UserTexts(parentTurn!).ShouldNotContain(ForkPrompt);

            var parentTexts = await WaitForAsync(
                async () => MessageUserTexts((await normal.GetMessagesAsync(null, ct)).Messages),
                texts => texts.Contains(ParentPrompt),
                ct);
            parentTexts.ShouldBe([NormalPrompt, ParentPrompt]);
            MessageUserTexts((await fork.GetMessagesAsync(null, ct)).Messages).ShouldBe([NormalPrompt, ForkPrompt]);
        });
    }

    private static List<string> MessageUserTexts(IReadOnlyList<HarnessMessage> messages)
        => [.. messages.Where(m => m.Role == "user").Select(m => string.Concat(m.Parts.OfType<TextPart>().Select(p => p.Text)))];
}

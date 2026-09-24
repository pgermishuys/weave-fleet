extern alias FakeLlm;

using FakeLlm::FakeLlmServer;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.Workflows;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.IntegrationTests.Harnesses.OpenCode2;

/// <summary>
/// Drafting a workflow on OpenCode 2: Save as workflow… asks V2's <c>generate</c> about the session, and a first answer
/// with errors is asked about again with the earlier question and answer carried in the prompt; Describe it asks
/// <c>generate</c> on a throwaway session, then deletes it. Neither leaves anything in a session's history or on disk.
/// </summary>
public sealed partial class OpenCode2LiveTests
{
    private const string DraftWithErrors = """
        ```yaml
        name: Live draft
        steps:
          - id: look
            title: Look
            model: standard
            prompt: |
              The request: {{request}}
            outcomes: [done, again]
            on: { again: look }
        ```
        """;

    private const string DraftFixed = """
        ```yaml
        name: Live draft
        steps:
          - id: look
            title: Look
            model: standard
            prompt: |
              The request: {{request}}
            outcomes: [done, again]
            on: { again: look, max: 2 }
        ```
        """;

    /// <summary>The draft questions, with or without tools in the request: the retry first, since it quotes the question.</summary>
    private static ScriptedLlmResponse? AnswerDraft(string request)
    {
        var text = LlmRequest.LastUserText(request) ?? string.Empty;
        if (text.Contains("That file has errors:", StringComparison.Ordinal))
            return new ScriptedLlmResponse { Text = DraftFixed };
        if (text.Contains("Describe the process this session followed as a Fleet workflow", StringComparison.Ordinal))
            return new ScriptedLlmResponse { Text = DraftWithErrors };
        if (text.StartsWith("Write a Fleet workflow that does this:", StringComparison.Ordinal))
            return new ScriptedLlmResponse { Text = DraftFixed };
        return null;
    }

    [OpenCode2Fact]
    public async Task A_session_drafts_a_workflow_off_the_record_with_one_retry_and_its_history_is_unchanged()
    {
        const string prompt = "Look around the folder. (workflow drafts)";
        fleet.Answer(request => LlmRequest.Starts(request, prompt) ? new ScriptedLlmResponse { Text = "It holds a readme." } : null);
        fleet.Answer(AnswerDraft);
        fleet.Llm.Queue.ToolLessAnswer = AnswerDraft;
        using var cts = new CancellationTokenSource(Timeout);
        var folder = fleet.NewFolder("workflow-drafts");
        WorkflowLiveGit.Init(folder);
        try
        {
            var id = await fleet.CreateSessionAsync(folder, "Look around", cts.Token);
            var events = fleet.Watch(cts.Token, id);
            await PromptAsync(id, prompt, options: null, cts.Token);
            await WaitForAsync(events, () => events.For(id).Any(e => e.Type == "session.idle"), cts.Token);
            var harness = await fleet.HarnessSessionAsync(id, cts.Token);
            var history = (await harness.GetMessagesAsync(null, cts.Token)).Messages.Select(m => m.Id).ToList();

            var result = await DrafterAsync(drafter => drafter.FromSessionAsync(id, cts.Token));

            result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Description : null);
            var drafted = result.Value;
            (drafted.Asks, drafted.Check.IsValid, drafted.Check.Draft!.Name, drafted.SessionTitle).ShouldBe((2, true, "Live draft", "Look around"));
            // V2 reports no tokens for generate.
            drafted.Tokens.ShouldBeNull();

            // The follow-up carried the question, the first answer and the errors.
            var retry = fleet.Llm.Queue.Requests.Single(r => (LlmRequest.LastUserText(r) ?? "").Contains("That file has errors:", StringComparison.Ordinal));
            var retryText = LlmRequest.LastUserText(retry)!;
            retryText.ShouldStartWith("Describe the process this session followed as a Fleet workflow");
            retryText.ShouldContain("Your answer was:");
            retryText.ShouldContain("on: { again: look }");
            retryText.ShouldContain("max");

            // Nothing reached the session's history, and nothing was written.
            (await harness.GetMessagesAsync(null, cts.Token)).Messages.Select(m => m.Id).ShouldBe(history);
            Directory.Exists(Path.Combine(folder, ".weave")).ShouldBeFalse();
            WorkflowLiveGit.Run(folder, "status", "--porcelain").ShouldBeEmpty();
        }
        finally
        {
            fleet.Llm.Queue.ToolLessAnswer = null;
        }
    }

    [OpenCode2Fact]
    public async Task A_description_is_drafted_on_a_throwaway_session_with_the_folders_default_model()
    {
        fleet.Answer(AnswerDraft);
        fleet.Llm.Queue.ToolLessAnswer = AnswerDraft;
        using var cts = new CancellationTokenSource(Timeout);
        var folder = fleet.NewFolder("workflow-describe");
        WorkflowLiveGit.Init(folder);
        try
        {
            var result = await DrafterAsync(drafter => drafter.FromDescriptionAsync(folder, "Look around the folder and say what's in it.", "opencode2", null, cts.Token));

            result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Description : null);
            (result.Value.Asks, result.Value.Check.IsValid, result.Value.SessionTitle).ShouldBe((1, true, null));
            var asked = fleet.Llm.Queue.Requests.Single(r => (LlmRequest.LastUserText(r) ?? "").StartsWith("Write a Fleet workflow that does this:", StringComparison.Ordinal));
            LlmRequest.LastUserText(asked)!.ShouldContain("Look around the folder and say what's in it.");
            LlmRequest.Model(asked).ShouldBe("fake-model");
            Directory.Exists(Path.Combine(folder, ".weave")).ShouldBeFalse();
        }
        finally
        {
            fleet.Llm.Queue.ToolLessAnswer = null;
        }
    }

    private async Task<T> DrafterAsync<T>(Func<WorkflowDrafter, Task<T>> call)
    {
        using var user = BackgroundUserContext.BeginScope(OpenCode2LiveFleet.Owner);
        using var scope = fleet.Services.CreateScope();
        return await call(scope.ServiceProvider.GetRequiredService<WorkflowDrafter>());
    }
}

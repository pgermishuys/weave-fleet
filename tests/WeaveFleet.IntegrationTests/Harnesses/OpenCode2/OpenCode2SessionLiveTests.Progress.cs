extern alias FakeLlm;

using FakeLlm::FakeLlmServer;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Progress;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.IntegrationTests.Harnesses.OpenCode2;

/// <summary>
/// Session progress on OpenCode 2: a plan file the model writes and ticks with V2's <c>write</c> and <c>edit</c>, and a
/// subagent started for a plan step shows under that step. These also pin the shapes of V2's file tools: if a release
/// changes them, these fail.
/// </summary>
public sealed partial class OpenCode2SessionLiveTests
{
    private const string PlanPath = ".weave/plans/drop-tables.md";

    [OpenCode2Fact]
    public async Task A_plan_file_the_model_writes_and_ticks_becomes_the_sessions_progress()
    {
        const string prompt = "Plan the work, then start on it. (progress: plan)";
        fleet.Answer(request => !LlmRequest.Asked(request, prompt) ? null
            : LlmRequest.Starts(request, prompt) ? ToolCall("call_plan_write", "write", new
            {
                path = PlanPath,
                content = "# Drop the dead tables\n\n## Tasks\n\n- [ ] 1. Write the migration\n- [ ] 2. Drop the indexes\n- [ ] 3. Start the app on a fresh database\n",
            })
            : LlmRequest.LastToolText(request) is not null && !request.Contains("call_plan_edit", StringComparison.Ordinal)
                ? ToolCall("call_plan_edit", "edit", new { path = PlanPath, oldString = "- [ ] 1. Write the migration", newString = "- [x] 1. Write the migration" })
            : LlmRequest.Continues(request, prompt) ? new ScriptedLlmResponse { Text = "Started." }
            : null);
        using var cts = new CancellationTokenSource(Timeout);
        var folder = fleet.NewFolder("progress-plan");
        var id = await fleet.CreateSessionAsync(folder, "Plan progress", cts.Token);
        var events = fleet.WatchTopics(["sessions", $"session:{id}"], cts.Token);

        await PromptAsync(id, prompt, options: null, cts.Token);
        await WaitForAsync(events, () => Rows(events, id).Any(row => row.Contains("\"done\":1", StringComparison.Ordinal)), cts.Token);

        // Written: a plan at 0 of 3. Ticked: 1 of 3.
        Rows(events, id).ShouldBe(
        [
            $$"""{"sessionId":"{{id}}","kind":"plan","done":0,"total":3,"current":"Write the migration"}""",
            $$"""{"sessionId":"{{id}}","kind":"plan","done":1,"total":3,"current":"Drop the indexes"}""",
        ]);

        // The tick has the time Fleet saw it and the message of the edit that made it.
        var plan = (await StoredProgressAsync(id, cts.Token)).ShouldNotBeNull().Plans.ShouldHaveSingleItem();
        (plan.Path, plan.Title).ShouldBe((PlanPath, "Drop the dead tables"));
        var first = plan.Steps.First();
        first.Checked.ShouldBeTrue();
        first.TickedAt.ShouldNotBeNull();
        first.TickedInMessageId.ShouldNotBeNullOrWhiteSpace();
        File.ReadAllText(Path.Combine(folder, ".weave", "plans", "drop-tables.md")).ShouldContain("- [x] 1. Write the migration");
    }

    [OpenCode2Fact]
    public async Task A_subagent_started_for_a_step_shows_under_that_step()
    {
        const string prompt = "Plan the work, then hand the first step to a subagent. (progress: subagent)";
        const string childPrompt = "Write the migration that drops the dead tables. (progress: child)";
        fleet.Answer(request => LlmRequest.Starts(request, childPrompt) ? new ScriptedLlmResponse { Text = "The migration is written." }
            : !LlmRequest.Asked(request, prompt) ? null
            : LlmRequest.Starts(request, prompt) ? ToolCall("call_sub_plan", "write", new
            {
                path = PlanPath,
                content = "# Drop the dead tables\n\n- [ ] 1. Write the migration\n- [ ] 2. Drop the indexes\n- [ ] 3. Start the app\n",
            })
            : LlmRequest.Continues(request, prompt) && !request.Contains("call_sub_step", StringComparison.Ordinal)
                ? ToolCall("call_sub_step", "subagent", new { description = "Write the migration", prompt = childPrompt, agent = "general", subagent_type = "general" })
            : LlmRequest.Continues(request, prompt) ? new ScriptedLlmResponse { Text = "Step 1 is done." }
            : null);
        using var cts = new CancellationTokenSource(Timeout);
        var id = await fleet.CreateSessionAsync(fleet.NewFolder("progress-subagent"), "Subagent progress", cts.Token);
        var events = fleet.Watch(cts.Token, id);

        await PromptAsync(id, prompt, options: null, cts.Token);

        // The parent shows the subagent under step 1, titled with its task, linked to its session, finished.
        var stored = await WaitForAsync(events, async () =>
            await StoredProgressAsync(id, cts.Token) is { Subagents: [{ Status: "completed" }] } progress ? progress : null, cts.Token);
        (stored.Kind, stored.Done, stored.Total).ShouldBe((SessionProgressKinds.Plan, 0, 3));
        var subagent = stored.Subagents.ShouldHaveSingleItem();
        (subagent.Title, subagent.StepKey, subagent.Status).ShouldBe(("Write the migration", "1", "completed"));
        subagent.ChildSessionId.ShouldNotBeNullOrWhiteSpace();
        (await Delegations(id)).ShouldContain(d => d.ChildSessionId == subagent.ChildSessionId);
    }

    /// <summary>The row summaries Fleet sent on <c>sessions</c> for <paramref name="sessionId"/>, as JSON.</summary>
    private static List<string> Rows(LiveEvents events, string sessionId)
        => events.All
            .Where(e => e.Topic == "sessions" && e.Type == SessionProgressTracker.SummaryEventType
                && e.Payload.GetProperty("sessionId").GetString() == sessionId)
            .Select(e => e.Payload.GetRawText())
            .ToList();

    private async Task<SessionProgress?> StoredProgressAsync(string sessionId, CancellationToken ct)
    {
        using var scope = fleet.Services.CreateScope();
        using var user = scope.ServiceProvider.GetRequiredService<IBackgroundUserScope>().Begin(OpenCode2LiveFleet.Owner);
        return await scope.ServiceProvider.GetRequiredService<ISessionProgressRepository>().GetAsync(sessionId, ct);
    }
}

extern alias FakeLlm;

using System.Diagnostics;
using FakeLlm::FakeLlmServer;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;

namespace WeaveFleet.IntegrationTests.Harnesses.OpenCode2;

/// <summary>
/// V2 2.3.0 runs every subagent in the background and its child sends a <c>&lt;subagent&gt;</c> report each time it goes
/// idle. A later round reports again: that is one delegation and one report per round, never a second card; a child
/// idle with a shell still running doesn't end the delegation; and a server that dies under a running child leaves
/// one ended delegation, no report and no detached shell daemon behind.
/// </summary>
public sealed partial class OpenCode2LiveTests
{
    private static List<string> ReportsOf(LiveEvents events, string id)
        => LatestParts<TextMessageEventPart>(events, id).Select(p => p.Text).Where(t => Notice.Is(t, "subagent")).ToList();

    [OpenCode2Fact]
    public async Task A_child_that_goes_idle_twice_is_one_delegation_with_one_report_per_round()
    {
        const string prompt = "Hand it over, twice. (two rounds)";
        const string childPrompt = "child-two-rounds-first";
        const string roundTwo = "child-two-rounds-second";
        fleet.Answer(request =>
        {
            if (LlmRequest.Starts(request, roundTwo))
                return new ScriptedLlmResponse { Text = "Round two done." };
            if (LlmRequest.Starts(request, childPrompt))
                return new ScriptedLlmResponse { Text = "Round one done." };
            if (LlmRequest.Starts(request, prompt))
                return ToolCall("call_two_rounds", "subagent", new { description = "A two-round helper", prompt = childPrompt, agent = "general", subagent_type = "general" });
            if (LlmRequest.Continues(request, prompt))
                return new ScriptedLlmResponse { Text = "Handed over." };
            if (LlmRequest.StartsNotice(request, "subagent") && request.Contains("A two-round helper", StringComparison.Ordinal))
                return new ScriptedLlmResponse { Text = "Noted." };
            return null;
        });

        using var cts = new CancellationTokenSource(Timeout);
        var id = await fleet.CreateSessionAsync(fleet.NewFolder("two-rounds"), "Two rounds", cts.Token);
        var events = fleet.Watch(cts.Token, id);
        await PromptAsync(id, prompt, options: null, cts.Token);

        var delegation = await WaitForAsync(events, async () => (await Delegations(id))
            .SingleOrDefault(d => d.ParentToolCallId == "call_two_rounds" && d.Status == "completed" && d.ChildSessionId is not null), cts.Token);
        await WaitForAsync(events, () => ReportsOf(events, id).Count == 1, cts.Token);

        // A second round in the same child: it reports again.
        await PromptAsync(delegation.ChildSessionId!, roundTwo, options: null, cts.Token);
        await WaitForAsync(events, () => ReportsOf(events, id).Count >= 2, cts.Token);
        await Task.Delay(5000, cts.Token);

        var reports = ReportsOf(events, id);
        reports.Count.ShouldBe(2, string.Join(" || ", reports));
        reports[0].ShouldContain("Round one done.");
        reports[1].ShouldContain("Round two done.");
        var delegations = (await Delegations(id)).Where(d => d.ParentToolCallId == "call_two_rounds").ToList();
        delegations.Count.ShouldBe(1, string.Join(";", delegations.Select(d => $"{d.Status}:{d.ChildSessionId}")));
        LatestParts<ToolMessageEventPart>(events, id).Count(p => p.CallId == "call_two_rounds").ShouldBe(1);
    }

    [OpenCode2Fact]
    public async Task A_child_idle_with_a_shell_still_running_keeps_its_delegation_open()
    {
        const string prompt = "Hand it over, shell running. (idle with shell)";
        const string childPrompt = "child-leaves-a-shell";
        fleet.Answer(request =>
        {
            if (LlmRequest.Starts(request, childPrompt))
                return ToolCall("call_child_bg_shell", "shell", new { command = "sleep 10; echo late", description = "A job the child leaves", background = true });
            if (LlmRequest.Continues(request, childPrompt))
                return new ScriptedLlmResponse { Text = "Child idle, shell running." };
            if (LlmRequest.Starts(request, prompt))
                return ToolCall("call_idle_shell", "subagent", new { description = "A helper with a shell", prompt = childPrompt, agent = "general", subagent_type = "general" });
            if (LlmRequest.Continues(request, prompt))
                return new ScriptedLlmResponse { Text = "Handed over." };
            if (LlmRequest.StartsNotice(request, "subagent") && request.Contains("A helper with a shell", StringComparison.Ordinal))
                return new ScriptedLlmResponse { Text = "Noted." };
            return null;
        });

        using var cts = new CancellationTokenSource(Timeout);
        var id = await fleet.CreateSessionAsync(fleet.NewFolder("idle-shell"), "Idle with a shell", cts.Token);
        var events = fleet.Watch(cts.Token, id);
        await PromptAsync(id, prompt, options: null, cts.Token);

        // The child's own turn ends while its shell runs for 10 s.
        await WaitForAsync(events, () => LatestParts<TextMessageEventPart>(events, id).Any(p => p.Text == "Noted.") || ReportsOf(events, id).Count > 0, cts.Token);
        var delegation = (await Delegations(id)).Single(d => d.ParentToolCallId == "call_idle_shell");
        delegation.Status.ShouldNotBe("completed", $"ended with the child's shell still running: {string.Join(" || ", ReportsOf(events, id))}");

        // The shell ends, the child reports again, and only then is the delegation done (and the server has no work left).
        await WaitForAsync(events, async () => (await Delegations(id)).Single(d => d.ParentToolCallId == "call_idle_shell").Status == "completed", cts.Token);
    }
}

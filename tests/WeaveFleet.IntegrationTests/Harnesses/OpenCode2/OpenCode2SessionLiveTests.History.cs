extern alias FakeLlm;

using FakeLlm::FakeLlmServer;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.IntegrationTests.Harnesses.OpenCode2;

/// <summary>
/// The Fleet API skill every session gets reaches the model and takes it to Fleet, and a session V2 compacted still
/// opens with its whole history.
/// </summary>
public sealed partial class OpenCode2SessionLiveTests
{
    [OpenCode2Fact]
    public async Task A_session_loads_the_fleet_api_skill_and_reaches_Fleet_with_it()
    {
        const string prompt = "How is this session doing in Fleet? (fleet skill)";
        string? id = null;
        fleet.Answer(request => LlmRequest.Starts(request, prompt) ? ToolCall("call_skill", "skill", new { id = "fleet" })
            : LlmRequest.Continues(request, prompt) && LlmRequest.LastToolText(request) is { } text && text.Contains(SkillText, StringComparison.Ordinal)
                ? ToolCall("call_fleet_api", "shell", new { command = $"curl -s \"$FLEET_URL/api/sessions/{id}\"", description = "Read this session from Fleet" })
            : LlmRequest.Continues(request, prompt) ? new ScriptedLlmResponse { Text = "Done." }
            : null);
        using var cts = new CancellationTokenSource(Timeout);
        id = await fleet.CreateSessionAsync(fleet.NewFolder("fleet-skill"), "Fleet skill", cts.Token);
        var events = fleet.Watch(cts.Token, id);

        await PromptAsync(id, prompt, options: null, cts.Token);
        await WaitForAsync(events, () => events.For(id).Any(e => e.Type == "session.idle"), cts.Token);

        // The skill's own text reached the model, then the API's answer about this session, fetched with no token.
        var results = fleet.Llm.Queue.Requests.Where(r => LlmRequest.Continues(r, prompt)).Select(r => LlmRequest.LastToolText(r) ?? "").ToList();
        results.ShouldContain(text => text.Contains(SkillText, StringComparison.Ordinal), string.Join("\n---\n", results));
        results.ShouldContain(text => text.Contains("workspaceDirectory", StringComparison.Ordinal) && text.Contains("activityStatus", StringComparison.Ordinal),
            string.Join("\n---\n", results));
    }

    /// <summary>A line of the <c>fleet</c> skill (<c>opencode/skills/fleet/SKILL.md</c>).</summary>
    private const string SkillText = "Look it up, don't guess";

    [OpenCode2Fact]
    public async Task A_session_V2_compacted_still_opens_with_its_history()
    {
        const string ask = "Where is the login page? (compacted)";
        const string answer = "The login page lives in client/src/pages/login.vue.";
        const string summary = "## Objective\n- Find the login page.\n\n## Next Move\n1. (none)";
        fleet.Answer(request => LlmRequest.Starts(request, ask) ? new ScriptedLlmResponse { Text = answer, InputTokens = 40_000, OutputTokens = 20 }
            // V2 keeps a summary only when it fills in V2's template, headings and all.
            : IsCompactionRequest(request) && request.Contains("(compacted)", StringComparison.Ordinal) ? new ScriptedLlmResponse { Text = summary }
            : null);
        using var cts = new CancellationTokenSource(Timeout);
        var id = await fleet.CreateSessionAsync(fleet.NewFolder("compacted"), "Compacted", cts.Token);
        var events = fleet.Watch(cts.Token, id);

        await PromptAsync(id, ask, options: null, cts.Token);
        await WaitForAsync(events, () => LatestParts<TextMessageEventPart>(events, id).Any(p => p.Text == answer) && events.For(id).Any(e => e.Type == "session.idle"), cts.Token);
        (await fleet.WithOrchestratorAsync(o => o.CompactAsync(id, cts.Token))).IsSuccess.ShouldBeTrue();
        var compacted = await WaitForAsync(events, async () =>
            await CompactionOf(id) is { } context && (context.CompactedAt is not null || context.CompactionError is not null) ? context : null, cts.Token);
        compacted.CompactionError.ShouldBeNull();

        // Opening the session: the client subscribes and gets this snapshot, read from V2's history.
        SessionSnapshot snapshot;
        using (var scope = fleet.Services.CreateScope())
        using (scope.ServiceProvider.GetRequiredService<IBackgroundUserScope>().Begin(OpenCode2LiveFleet.Owner))
            snapshot = await scope.ServiceProvider.GetRequiredService<ISessionMessageProxy>().GetSnapshotAsync(id, ct: cts.Token);

        snapshot.IsPartial.ShouldBeFalse();
        var parts = snapshot.Messages.SelectMany(m => m.Parts).ToList();
        parts.OfType<TextMessageEventPart>().Select(p => p.Text).ShouldContain(answer);
        parts.OfType<CompactionMessageEventPart>().ShouldHaveSingleItem().Summary.ShouldBe(summary);
    }

    private async Task<WeaveFleet.Domain.Entities.SessionContext?> CompactionOf(string sessionId)
    {
        using var scope = fleet.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ISessionContextRepository>().GetForOwnerAsync(sessionId, OpenCode2LiveFleet.Owner, CancellationToken.None);
    }

    /// <summary>V2 asks for a compaction's summary with its template.</summary>
    private static bool IsCompactionRequest(string request)
        => request.Contains("You MUST use this format for your response", StringComparison.Ordinal);
}

extern alias FakeLlm;

using FakeLlm::FakeLlmServer;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.IntegrationTests.Harnesses.OpenCode2;

/// <summary>
/// How full the context is on a real V2: a step's tokens reach Fleet's record with the model's window from V2's model
/// list, and Compact now has V2 compact the session. This fails if a future V2 renames its step usage, its model
/// limits, its compact route or its compaction events.
/// </summary>
public sealed partial class OpenCode2LiveTests
{
    [OpenCode2Fact]
    public async Task A_step_fills_the_context_and_compact_now_empties_it()
    {
        const string ask = "Where is the login page? (context)";
        fleet.Answer(request => LlmRequest.Starts(request, ask)
            ? new ScriptedLlmResponse { Text = "In client/src/pages/login.vue.", InputTokens = 40_000, OutputTokens = 20 }
            // V2 keeps a summary only when it fills in V2's template, headings and all.
            : IsCompactionRequest(request) && request.Contains("(context)", StringComparison.Ordinal)
                ? new ScriptedLlmResponse { Text = "## Objective\n- Find the login page.\n\n## Next Move\n1. (none)" }
                : null);

        using var cts = new CancellationTokenSource(Timeout);
        var id = await fleet.CreateSessionAsync(fleet.NewFolder("context"), "Context", cts.Token);
        var events = fleet.Watch(cts.Token, id);
        await PromptAsync(id, ask, options: null, cts.Token);

        // The step's tokens, and then the window V2's model list gives the model.
        var filled = (SessionContext)await WaitForAsync(events, async () =>
            await ContextOf(id) is { Used: > 0, Limit: not null, Turns.Count: 1 } context ? context : null, cts.Token);
        filled.Used.ShouldBe(40_020);
        filled.Limit.ShouldBe(300_000);
        filled.ModelId.ShouldBe("fake-model");

        // Compact now: V2 takes it into the session's inbox and says when it's done.
        (await fleet.WithOrchestratorAsync(o => o.CompactAsync(id, cts.Token))).IsSuccess.ShouldBeTrue();
        var compacted = (SessionContext)await WaitForAsync(events, async () =>
            await ContextOf(id) is { } context && (context.CompactedAt is not null || context.CompactionError is not null) ? context : null, cts.Token);
        compacted.CompactionError.ShouldBeNull();
        compacted.CompactedAt.ShouldNotBeNull();
        compacted.Used.ShouldBeNull();
        compacted.Compacting.ShouldBeFalse();
    }

    /// <summary>V2 asks for a compaction's summary with its template.</summary>
    private static bool IsCompactionRequest(string request)
        => request.Contains("You MUST use this format for your response", StringComparison.Ordinal);

    private async Task<SessionContext?> ContextOf(string sessionId)
    {
        using var scope = fleet.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ISessionContextRepository>().GetForOwnerAsync(sessionId, OpenCode2LiveFleet.Owner, CancellationToken.None);
    }
}

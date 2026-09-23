extern alias FakeLlm;

using FakeLlm::FakeLlmServer;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Skills;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.IntegrationTests.Harnesses.OpenCode2;

/// <summary>
/// Built-in skills live: they sit in one folder per owner that the owner's servers name once, so switching one on or
/// off reaches new sessions through V2's file watcher, with no new server. V2 fixes a session's skill list (the
/// <c>&lt;available_skills&gt;</c> block of its system prompt) when the session is created.
/// </summary>
public sealed partial class OpenCode2LiveTests
{
    private const string BuiltInSkill = "fleet-simplify";

    [OpenCode2Fact]
    public async Task A_built_in_skill_switched_on_reaches_new_sessions_on_the_same_server_and_off_leaves_them()
    {
        const string prompt = "Which skills do you have? (built-in skill)";
        fleet.Answer(request => LlmRequest.Starts(request, prompt) ? new ScriptedLlmResponse { Text = "These." } : null);
        using var cts = new CancellationTokenSource(Timeout);
        var folder = fleet.NewFolder("built-in-skill");

        var before = await fleet.CreateSessionAsync(folder, "Before", cts.Token);
        (await SystemPromptAsync(before, prompt, cts.Token)).ShouldNotContain(BuiltInSkill);
        var server = ((OpenCode2HarnessSession)await fleet.HarnessSessionAsync(before, cts.Token)).ProcessId.ShouldNotBeNull();

        try
        {
            await SetBuiltInSkillAsync(BuiltInSkill, enabled: true);
            var on = await fleet.CreateSessionAsync(folder, "Switched on", cts.Token);
            (await SystemPromptAsync(on, prompt, cts.Token)).ShouldContain(BuiltInSkill);
            ((OpenCode2HarnessSession)await fleet.HarnessSessionAsync(on, cts.Token)).ProcessId.ShouldBe(server);

            // A session that was running keeps the skills it started with.
            (await SystemPromptAsync(before, prompt, cts.Token)).ShouldNotContain(BuiltInSkill);

            await SetBuiltInSkillAsync(BuiltInSkill, enabled: false);
            var off = await fleet.CreateSessionAsync(folder, "Switched off", cts.Token);
            (await SystemPromptAsync(off, prompt, cts.Token)).ShouldNotContain(BuiltInSkill);
            ((OpenCode2HarnessSession)await fleet.HarnessSessionAsync(off, cts.Token)).ProcessId.ShouldBe(server);
        }
        finally
        {
            await SetBuiltInSkillAsync(BuiltInSkill, enabled: false);
        }
    }

    /// <summary>Switches a built-in skill as Settings does, then gives V2's file watcher a moment to rebuild.</summary>
    private async Task SetBuiltInSkillAsync(string name, bool enabled)
    {
        using (BackgroundUserContext.BeginScope(OpenCode2LiveFleet.Owner))
        using (var scope = fleet.Services.CreateScope())
        {
            var set = await scope.ServiceProvider.GetRequiredService<BuiltInSkillService>().SetEnabledAsync(name, enabled);
            set.IsSuccess.ShouldBeTrue(set.IsFailure ? set.Error.Description : null);
        }

        await Task.Delay(TimeSpan.FromSeconds(3));
    }

    /// <summary>Prompts the session and returns the system prompt of the model request its turn started with.</summary>
    private async Task<string> SystemPromptAsync(string sessionId, string prompt, CancellationToken ct)
    {
        var events = fleet.Watch(ct, sessionId);
        var asked = fleet.Llm.Queue.Requests.Count;
        await Task.Delay(300, ct);
        var idle = events.For(sessionId).Count(e => e.Type == "session.idle");
        await PromptAsync(sessionId, prompt, options: null, ct);
        await WaitForAsync(events, () => events.For(sessionId).Count(e => e.Type == "session.idle") > idle, ct);
        return LlmRequest.System(fleet.Llm.Queue.Requests.Skip(asked).Last(r => LlmRequest.Starts(r, prompt)));
    }
}

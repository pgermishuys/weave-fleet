extern alias FakeLlm;

using FakeLlm::FakeLlmServer;
using WeaveFleet.Domain.Events;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;

namespace WeaveFleet.IntegrationTests.Harnesses.OpenCode2;

/// <summary>
/// The agent's shell doesn't get what Fleet starts the V2 server with for the server alone: its password, its config
/// and database (the scratch setup sets both, as separate mode does), Fleet's config for it, and the bridge token. It
/// keeps <c>FLEET_URL</c>, which Fleet's skills call Fleet through. Fleet's plugin takes them out of every shell V2
/// starts, so a subagent's first command is covered as surely as the session's own.
/// </summary>
public sealed partial class OpenCode2LiveTests
{
    /// <summary>Names only: the values are secrets.</summary>
    private const string ListEnvironment = "env | cut -d= -f1 | sort | tr '\\n' ' '; echo; curl -s -o /dev/null -w 'fleet=%{http_code}' \"$FLEET_URL/api/sessions\"";

    private static readonly string[] ServerOnly =
    [
        "OPENCODE_SERVER_PASSWORD",
        "OPENCODE_CONFIG_DIR",
        "OPENCODE_DB",
        "OPENCODE_CONFIG",
        "OPENCODE_CONFIG_CONTENT",
        "FLEET_BRIDGE_TOKEN",
        "FLEET_SHELL_ENVIRONMENT",
    ];

    [OpenCode2Fact]
    public async Task The_agents_shell_and_its_subagents_do_not_get_the_servers_own_environment()
    {
        const string prompt = "List the environment. (shell environment)";
        const string childPrompt = "child-lists-the-environment";
        fleet.Answer(request =>
        {
            if (LlmRequest.Starts(request, childPrompt))
                return ToolCall("call_env_child", "shell", new { command = ListEnvironment, description = "The child's environment" });
            if (LlmRequest.Continues(request, childPrompt))
                return new ScriptedLlmResponse { Text = "Listed." };
            if (LlmRequest.Starts(request, prompt))
                return ToolCall("call_env", "shell", new { command = ListEnvironment, description = "The environment" });
            if (LlmRequest.Continues(request, prompt) && LlmRequest.LastToolText(request) is { } text && !text.Contains("Listed.", StringComparison.Ordinal))
                return ToolCall("call_env_sub", "subagent", new { description = "List it too", prompt = childPrompt, agent = "general", subagent_type = "general" });
            if (LlmRequest.Continues(request, prompt))
                return new ScriptedLlmResponse { Text = "Both listed." };
            return null;
        });

        using var cts = new CancellationTokenSource(Timeout);
        var id = await fleet.CreateSessionAsync(fleet.NewFolder("shell-environment"), "Shell environment", cts.Token);
        var events = fleet.Watch(cts.Token, id);
        await PromptAsync(id, prompt, options: null, cts.Token);
        await WaitForAsync(events, () => LatestParts<TextMessageEventPart>(events, id).Any(p => p.Text == "Both listed."), cts.Token);

        var own = ToolOutput(fleet.Llm.Queue.Requests.First(r => LlmRequest.Continues(r, prompt)));
        var child = ToolOutput(fleet.Llm.Queue.Requests.Single(r => LlmRequest.Continues(r, childPrompt)));
        foreach (var (who, output) in new[] { ("the session", own), ("its subagent", child) })
        {
            var names = output.Split('\n')[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            names.ShouldContain("PATH", $"{who}'s shell lost what it should have: {output}");
            names.ShouldContain("HOME", $"{who}'s shell lost what it should have: {output}");
            names.ShouldContain("FLEET_URL", $"{who}'s shell can't reach Fleet: {output}");
            names.Intersect(ServerOnly).ShouldBeEmpty($"{who}'s shell got the server's own environment: {output}");
            output.ShouldContain("fleet=200", customMessage: $"{who}'s FLEET_URL doesn't reach Fleet: {output}");
        }
    }

    [OpenCode2Fact]
    public async Task A_profile_session_back_on_a_new_server_after_an_idle_stop_still_does_not_get_it()
    {
        const string first = "List the environment. (env before idle)";
        const string second = "List it again. (env after idle)";
        fleet.Answer(request =>
        {
            if (LlmRequest.Starts(request, first))
                return ToolCall("call_env_before", "shell", new { command = ListEnvironment, description = "Before" });
            if (LlmRequest.Starts(request, second))
                return ToolCall("call_env_after", "shell", new { command = ListEnvironment, description = "After" });
            if (LlmRequest.Continues(request, first) || LlmRequest.Continues(request, second))
                return new ScriptedLlmResponse { Text = "Listed." };
            return null;
        });

        using var cts = new CancellationTokenSource(Timeout);
        var profile = await CreateProfileAsync("Environment", ProfileContent("environment-model"), cts.Token);
        var id = await CreateSessionAsync(fleet.NewFolder("profile-environment"), "Profile environment", profile.Id, cts.Token);
        var events = fleet.Watch(cts.Token, id);
        await PromptAsync(id, first, options: null, cts.Token);
        await WaitForAsync(events, () => events.For(id).Any(e => e.Type == "session.idle"), cts.Token);
        var harness = (OpenCode2HarnessSession)await fleet.HarnessSessionAsync(id, cts.Token);
        var server = harness.ProcessId.ShouldNotBeNull();

        await fleet.Runtime.StopIdleServersAsync(DateTimeOffset.UtcNow.AddHours(1));
        (await HasExitedAsync(server, TimeSpan.FromSeconds(10))).ShouldBeTrue();
        await PromptAsync(id, second, options: null, cts.Token);
        await WaitForAsync(events, () => events.For(id).Count(e => e.Type == "session.idle") >= 2, cts.Token);
        harness.ProcessId.ShouldNotBe(server);

        foreach (var prompt in new[] { first, second })
        {
            var output = ToolOutput(fleet.Llm.Queue.Requests.Single(r => LlmRequest.Continues(r, prompt)));
            var names = output.Split('\n')[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            names.ShouldContain("FLEET_URL", output);
            names.Intersect(ServerOnly).ShouldBeEmpty($"The shell got the server's own environment ({prompt}): {output}");
            output.ShouldContain("fleet=200", customMessage: output);
        }
    }

    private static string ToolOutput(string request) => LlmRequest.LastToolText(request).ShouldNotBeNull();
}

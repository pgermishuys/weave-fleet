extern alias FakeLlm;

using FakeLlm::FakeLlmServer;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;
using WeaveFleet.Infrastructure.Users;

namespace WeaveFleet.IntegrationTests.Harnesses.OpenCode2;

/// <summary>
/// An agent's own permissions on OpenCode 2. V2 decides a call from the agent's rules followed by the session's, and the
/// last rule that matches wins, so the rule for every action Fleet gives each session would override what the agent
/// denies; Fleet's plugin puts the agent's denies back on top. An agent that may not run shell commands or edit is
/// offered neither (nor <c>fleet_app_start</c>, which runs a command), and a scripted call to one comes to nothing;
/// what the agent allows still runs; an agent that denies nothing, and Fleet's own levels, work as they did. The
/// commands leave files behind, so the tests see whether they ran.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Harness", "opencode2")]
public sealed class OpenCode2AgentPermissionLiveTests(OpenCode2LiveFleet fleet) : OpenCode2LiveTestBase, IClassFixture<OpenCode2LiveFleet>
{
    protected override OpenCode2LiveFleet Fleet => fleet;

    /// <summary>A folder agent in OpenCode 1's format that may read but not run commands or edit.</summary>
    private const string ReadOnlyAgent = "readonly";

    /// <summary>A folder agent in OpenCode 2's own rule list that may not run shell commands, and may do everything else.</summary>
    private const string NoShellAgent = "noshell";

    /// <summary>A subagent that may not run shell commands, as a reviewer is declared.</summary>
    private const string ReadOnlySubagent = "readonly-helper";

    /// <summary>A folder agent that may run commands, except one that removes files.</summary>
    private const string NoRemoveAgent = "noremove";

    [OpenCode2Fact]
    public async Task An_agent_that_may_not_run_shell_commands_cannot_run_one_through_fleet_app_start()
    {
        var (ran, offered, answer) = await CallAsync("app-start-read-only", ReadOnlyAgent,
            "fleet_app_start", new { command = "echo ran> ran.txt", title = "diff" });

        offered.ShouldNotContain("shell");
        offered.ShouldNotContain("fleet_app_start");
        offered.ShouldNotContain("edit");
        offered.ShouldNotContain("write");
        offered.ShouldContain("read");
        ran.ShouldBeFalse("fleet_app_start ran a command for an agent that may not run shell commands.");
        answer.ShouldNotContain("before serving a page");
    }

    [OpenCode2Fact]
    public async Task An_agent_that_may_not_run_shell_commands_cannot_run_one_with_the_shell_tool()
    {
        var (ran, offered, _) = await CallAsync("shell-read-only", ReadOnlyAgent,
            "shell", new { command = "echo ran> ran.txt", description = "Leave a file" });

        offered.ShouldNotContain("shell");
        ran.ShouldBeFalse("The shell tool ran a command for an agent that may not run shell commands.");
    }

    [OpenCode2Fact]
    public async Task An_agent_without_a_permissions_block_still_runs_its_command_through_fleet_app_start()
    {
        var (ran, offered, _) = await CallAsync("app-start-allowed", agent: null,
            "fleet_app_start", new { command = "echo ran> ran.txt", title = "diff" });

        offered.ShouldContain("fleet_app_start");
        offered.ShouldContain("shell");
        offered.ShouldContain("edit");
        ran.ShouldBeTrue("fleet_app_start didn't run the command for an agent that may run shell commands.");
    }

    [OpenCode2Fact]
    public async Task An_agent_that_denies_shell_in_OpenCode_2s_own_rules_still_writes_files()
    {
        var folder = fleet.NewFolder("write-no-shell");
        var (ran, offered, _) = await CallAsync("write-no-shell", NoShellAgent,
            "write", new { path = Path.Combine(folder, "ran.txt"), content = "ran" }, folder);

        offered.ShouldNotContain("shell");
        offered.ShouldNotContain("fleet_app_start");
        offered.ShouldContain("write");
        ran.ShouldBeTrue("The write tool didn't run for an agent whose rules allow it.");
    }

    [OpenCode2Fact]
    public async Task A_command_the_agent_denies_doesnt_run_though_it_may_run_others()
    {
        const string name = "remove-denied";
        var folder = fleet.NewFolder(name);
        File.WriteAllText(Path.Combine(folder, "keep.txt"), "keep");

        var (ran, offered, answer) = await CallAsync(name, NoRemoveAgent,
            "shell", new { command = "rm keep.txt && echo ran> ran.txt", description = "Remove a file" }, folder);

        offered.ShouldContain("shell");
        ran.ShouldBeFalse("A command the agent's rules deny ran.");
        File.Exists(Path.Combine(folder, "keep.txt")).ShouldBeTrue();
        answer.ShouldContain("its own permissions deny it");

        var (allowed, _, _) = await CallAsync("remove-denied-other", NoRemoveAgent,
            "shell", new { command = "echo ran> ran.txt", description = "Leave a file" });
        allowed.ShouldBeTrue("A command the agent's rules allow didn't run.");
    }

    [OpenCode2Fact]
    public async Task A_subagent_that_may_not_run_shell_commands_isnt_offered_them_in_its_child_session()
    {
        const string prompt = "Ask the helper to look. (read-only subagent)";
        const string childPrompt = "Look at the code and leave a file.";
        fleet.Answer(request =>
            LlmRequest.Starts(request, childPrompt) ? ToolCall("call_child_shell", "shell", new { command = "echo ran> ran.txt", description = "Leave a file" })
            : LlmRequest.Continues(request, childPrompt) ? new ScriptedLlmResponse { Text = "Looked." }
            : LlmRequest.Starts(request, prompt) ? ToolCall("call_readonly_sub", "subagent", new { description = "Look", prompt = childPrompt, agent = ReadOnlySubagent, subagent_type = ReadOnlySubagent })
            : LlmRequest.Continues(request, prompt) ? new ScriptedLlmResponse { Text = "The helper looked." }
            : null);
        using var cts = new CancellationTokenSource(Timeout);
        var folder = fleet.NewFolder("subagent-read-only");
        WriteAgents(folder);

        var id = await fleet.CreateSessionAsync(folder, "Read-only subagent", cts.Token);
        var events = fleet.Watch(cts.Token, id);
        await PromptAsync(id, prompt, options: null, cts.Token);
        await WaitForAsync(events, () => fleet.Llm.Queue.Requests.Any(r => LlmRequest.Continues(r, prompt)), cts.Token);

        LlmRequest.OfferedToolNames(fleet.Llm.Queue.Requests.First(r => LlmRequest.Starts(r, prompt))).ShouldContain("shell");
        var child = LlmRequest.OfferedToolNames(fleet.Llm.Queue.Requests.First(r => LlmRequest.Starts(r, childPrompt)));
        child.ShouldNotContain("shell");
        child.ShouldNotContain("fleet_app_start");
        child.ShouldContain("read");
        File.Exists(Path.Combine(folder, "ran.txt")).ShouldBeFalse("A subagent that may not run shell commands ran one.");
    }

    [OpenCode2Fact]
    public async Task At_the_ask_level_an_agent_that_denies_nothing_asks_and_one_that_denies_shell_isnt_offered_it()
    {
        await SetLevelAsync(PermissionLevels.Ask);
        try
        {
            // An agent that denies nothing: the level asks, and Fleet puts the ask to the user.
            const string prompt = "Leave a file. (ask level)";
            fleet.Answer(request =>
                LlmRequest.Starts(request, prompt) ? ToolCall("call_ask_shell", "shell", new { command = "echo ran> ran.txt", description = "Leave a file" })
                : LlmRequest.Continues(request, prompt) ? new ScriptedLlmResponse { Text = "Done." }
                : null);
            using var cts = new CancellationTokenSource(Timeout);
            var folder = fleet.NewFolder("ask-level");
            WriteAgents(folder);
            var id = await fleet.CreateSessionAsync(folder, "Ask level", cts.Token);
            var events = fleet.Watch(cts.Token, id);
            await PromptAsync(id, prompt, options: null, cts.Token);

            var asked = await WaitForAsync(events, () => Task.FromResult(events.For(id).FirstOrDefault(e => e.Type == EventTypes.PermissionAsked)), cts.Token);
            File.Exists(Path.Combine(folder, "ran.txt")).ShouldBeFalse("The command ran before the user answered.");
            var requestId = asked.Payload.GetProperty("id").GetString()!;
            var replied = await fleet.WithOrchestratorAsync(o => o.ReplyToPermissionAsync(id, requestId, "once", null, cts.Token));
            replied.IsSuccess.ShouldBeTrue(replied.IsFailure ? replied.Error.Description : null);
            await WaitForAsync(events, () => fleet.Llm.Queue.Requests.Any(r => LlmRequest.Continues(r, prompt)), cts.Token);
            File.Exists(Path.Combine(folder, "ran.txt")).ShouldBeTrue("The command didn't run once the user allowed it.");

            // An agent that denies shell: not offered, and its scripted call neither asks nor runs.
            var (ran, offered, _) = await CallAsync("ask-level-read-only", ReadOnlyAgent,
                "shell", new { command = "echo ran> ran.txt", description = "Leave a file" }, noAskExpected: true);
            offered.ShouldNotContain("shell");
            ran.ShouldBeFalse();
        }
        finally
        {
            await SetLevelAsync(PermissionLevels.All);
        }
    }

    /// <summary>
    /// The scripted model calls <paramref name="tool"/> once as <paramref name="agent"/> (the default when null) in a new
    /// session. Returns whether <c>ran.txt</c> appeared, the tools the turn was offered, and the tool's answer.
    /// </summary>
    private async Task<(bool Ran, HashSet<string> Offered, string Answer)> CallAsync(
        string name, string? agent, string tool, object input, string? folder = null, bool noAskExpected = false)
    {
        var prompt = $"Do the thing. ({name})";
        fleet.Answer(request => LlmRequest.Starts(request, prompt)
            ? ToolCall($"call_{name}", tool, input)
            : LlmRequest.Continues(request, prompt) ? new ScriptedLlmResponse { Text = "Done." }
            : null);
        using var cts = new CancellationTokenSource(Timeout);
        folder ??= fleet.NewFolder(name);
        WriteAgents(folder);

        var id = await fleet.CreateSessionAsync(folder, name, cts.Token);
        var events = fleet.Watch(cts.Token, id);
        await PromptAsync(id, prompt, new PromptOptions { Agent = agent }, cts.Token);

        // Fleet answers fleet_app_start only once the command it ran has exited, and V2 the shell tool once its command
        // has, so by the time the model has the answer the file is there if it ran.
        await WaitForAsync(events, () => fleet.Llm.Queue.Requests.Any(r => LlmRequest.Continues(r, prompt)), cts.Token);
        if (noAskExpected)
            events.For(id).ShouldNotContain(e => e.Type == EventTypes.PermissionAsked);
        var offered = LlmRequest.OfferedToolNames(fleet.Llm.Queue.Requests.First(r => LlmRequest.Starts(r, prompt)));
        var answer = LlmRequest.LastToolText(fleet.Llm.Queue.Requests.First(r => LlmRequest.Continues(r, prompt))) ?? string.Empty;
        return (File.Exists(Path.Combine(folder, "ran.txt")), offered, answer);
    }

    /// <summary>The folder's own agents, in each of the formats V2 reads.</summary>
    private static void WriteAgents(string folder)
    {
        var agents = Path.Combine(folder, ".opencode", "agents");
        Directory.CreateDirectory(agents);
        File.WriteAllText(Path.Combine(agents, $"{ReadOnlyAgent}.md"), """
            ---
            description: Reads, never runs
            mode: primary
            permission:
              bash: deny
              edit: deny
            ---
            You read code and never change it.
            """);
        File.WriteAllText(Path.Combine(agents, $"{NoShellAgent}.md"), """
            ---
            description: Writes, never runs
            mode: primary
            permissions:
              - action: shell
                resource: "*"
                effect: deny
            ---
            You write files and never run commands.
            """);
        File.WriteAllText(Path.Combine(agents, $"{ReadOnlySubagent}.md"), """
            ---
            description: Looks, never runs
            mode: subagent
            permission:
              bash: deny
              edit: deny
            ---
            You look at code and never change it.
            """);
        File.WriteAllText(Path.Combine(agents, $"{NoRemoveAgent}.md"), """
            ---
            description: Runs commands but never removes files
            mode: primary
            permissions:
              - action: shell
                resource: "rm *"
                effect: deny
            ---
            You run commands but never remove files.
            """);
    }

    /// <summary>Settings → Permissions for OpenCode 2, as the owner sets it.</summary>
    private async Task SetLevelAsync(string level)
    {
        using var user = BackgroundUserContext.BeginScope(OpenCode2LiveFleet.Owner);
        using var scope = fleet.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IUserPreferenceRepository>()
            .SetAsync(SessionPermissions.HarnessLevelKey(OpenCode2HarnessSession.Type), level);
    }
}

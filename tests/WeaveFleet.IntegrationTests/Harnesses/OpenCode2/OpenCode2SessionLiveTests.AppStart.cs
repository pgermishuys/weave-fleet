extern alias FakeLlm;

using FakeLlm::FakeLlmServer;

namespace WeaveFleet.IntegrationTests.Harnesses.OpenCode2;

/// <summary>
/// <c>fleet_app_start</c> runs a shell command, so Fleet's plugin puts it under V2's shell permission: an agent that may
/// run shell commands is offered it and runs its command. The command leaves a file behind, so the test sees whether
/// it ran.
/// </summary>
public sealed partial class OpenCode2SessionLiveTests
{
    [OpenCode2Fact]
    public async Task An_agent_that_may_run_shell_commands_runs_its_command_through_fleet_app_start()
    {
        const string prompt = "Show me the diff. (app start)";
        fleet.Answer(request => LlmRequest.Starts(request, prompt)
            ? ToolCall("call_app_start", "fleet_app_start", new { command = "echo ran> ran.txt", title = "diff" })
            : LlmRequest.Continues(request, prompt) ? new ScriptedLlmResponse { Text = "Done." }
            : null);
        using var cts = new CancellationTokenSource(Timeout);
        var folder = fleet.NewFolder("app-start");
        var id = await fleet.CreateSessionAsync(folder, "App start", cts.Token);
        var events = fleet.Watch(cts.Token, id);

        await PromptAsync(id, prompt, options: null, cts.Token);

        // Fleet answers only once a command it ran has exited, so by the time the model has the answer the file is
        // there if it ran.
        await WaitForAsync(events, () => fleet.Llm.Queue.Requests.Any(r => LlmRequest.Continues(r, prompt)), cts.Token);
        LlmRequest.OfferedToolNames(fleet.Llm.Queue.Requests.First(r => LlmRequest.Starts(r, prompt))).ShouldContain("fleet_app_start");
        File.Exists(Path.Combine(folder, "ran.txt")).ShouldBeTrue("fleet_app_start didn't run the command for an agent that may run shell commands.");
    }
}

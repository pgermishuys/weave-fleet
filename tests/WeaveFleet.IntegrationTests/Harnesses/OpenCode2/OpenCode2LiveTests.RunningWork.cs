extern alias FakeLlm;

using FakeLlm::FakeLlmServer;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;
using WeaveFleet.Infrastructure.Users;

namespace WeaveFleet.IntegrationTests.Harnesses.OpenCode2;

/// <summary>
/// Work a real V2 runs in the background is Fleet's running work: a shell Fleet can read and stop on its own, and a
/// subagent whose child session Fleet stops by interrupting it. Each leaves the rest of the session as it was.
/// </summary>
public sealed partial class OpenCode2LiveTests
{
    [OpenCode2Fact]
    public async Task A_background_shell_is_running_work_whose_output_fleet_reads_and_which_fleet_stops()
    {
        const string prompt = "Start the server and carry on. (running work shell)";
        fleet.Answer(request =>
        {
            if (LlmRequest.Starts(request, prompt))
                return ToolCall("call_rw_shell", "shell", new { command = "echo server-ready; sleep 300; echo never-printed", description = "A dev server", background = true });
            if (LlmRequest.Continues(request, prompt))
                return new ScriptedLlmResponse { Text = "The server is running." };
            // The notice that the stopped shell ended wakes the session. Only this test's: the first answer that matches wins.
            if (LlmRequest.Starts(request, "<shell") && request.Contains("server-ready", StringComparison.Ordinal))
                return new ScriptedLlmResponse { Text = "Noted, the server stopped." };
            return null;
        });

        using var cts = new CancellationTokenSource(Timeout);
        var id = await fleet.CreateSessionAsync(fleet.NewFolder("running-work-shell"), "Running work shell", cts.Token);
        var events = fleet.Watch(cts.Token, id);

        await PromptAsync(id, prompt, options: null, cts.Token);
        await WaitForAsync(events, () => events.For(id).Any(e => e.Type == "session.idle"), cts.Token);

        // The call returned; its shell is running work Fleet can stop and read.
        var work = await WaitForAsync(events, async () => (await Work(id)).SingleOrDefault(w => w.Kind == WorkKinds.Shell), cts.Token);
        work.Status.ShouldBe("running");
        work.Background.ShouldBeTrue();
        work.CanStop.ShouldBeTrue();
        work.CanReadOutput.ShouldBeTrue();
        work.ToolCallId.ShouldBe("call_rw_shell");
        work.Label.ShouldBe("echo server-ready; sleep 300; echo never-printed");
        work.WorkId.ShouldStartWith("sh_");

        var output = await WaitForAsync(events, async () =>
        {
            var page = await WithOrchestratorAsync(o => o.ReadWorkOutputAsync(id, work.Id, 0, cts.Token));
            return page.IsSuccess && page.Value.Text.Contains("server-ready", StringComparison.Ordinal) ? page.Value : null;
        }, cts.Token);
        output.NextOffset.ShouldBe(output.Size);
        var rest = await WithOrchestratorAsync(o => o.ReadWorkOutputAsync(id, work.Id, output.NextOffset, cts.Token));
        rest.Value.Text.ShouldBeEmpty();

        var stopped = await WithOrchestratorAsync(o => o.StopWorkAsync(id, work.Id, cts.Token));

        stopped.IsSuccess.ShouldBeTrue(stopped.IsFailure ? stopped.Error.Description : null);
        stopped.Value.Status.ShouldBe("cancelled");
        stopped.Value.EndedReason.ShouldBe(WorkEndedReasons.Cancelled);
        var harness = (OpenCode2HarnessSession)await fleet.HarnessSessionAsync(id, cts.Token);
        (await harness.GetRunningWorkAsync(cts.Token)).ShouldNotBeNull().ShouldNotContain(w => w.WorkId == work.WorkId);
        // V2's own word on it afterwards changes nothing.
        await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);
        (await Work(id)).Single(w => w.Id == work.Id).EndedReason.ShouldBe(WorkEndedReasons.Cancelled);
    }

    [OpenCode2Fact]
    public async Task A_background_subagent_is_running_work_in_its_child_session_which_fleet_stops()
    {
        const string prompt = "Hand it over and carry on. (running work subagent)";
        const string childPrompt = "child-works-for-a-long-time";
        fleet.Answer(request =>
        {
            if (LlmRequest.Starts(request, childPrompt))
                return ToolCall("call_rw_child_work", "shell", new { command = "sleep 300; echo child-worked", description = "The child's work" });
            if (LlmRequest.Starts(request, prompt))
                return ToolCall("call_rw_sub", "subagent", new { description = "A long helper", prompt = childPrompt, agent = "general", subagent_type = "general", background = true });
            if (LlmRequest.Continues(request, prompt))
                return new ScriptedLlmResponse { Text = "The helper is on it." };
            if (LlmRequest.Starts(request, "<subagent") && request.Contains("A long helper", StringComparison.Ordinal))
                return new ScriptedLlmResponse { Text = "The helper was stopped." };
            return null;
        });

        using var cts = new CancellationTokenSource(Timeout);
        var id = await fleet.CreateSessionAsync(fleet.NewFolder("running-work-subagent"), "Running work subagent", cts.Token);
        var events = fleet.Watch(cts.Token, id);

        await PromptAsync(id, prompt, options: null, cts.Token);
        await WaitForAsync(events, () => events.For(id).Any(e => e.Type == "session.idle"), cts.Token);

        var work = await WaitForAsync(events, async () => (await Work(id))
            .SingleOrDefault(w => w.Kind == WorkKinds.Subagent && w.ChildSessionId is not null && w.Background), cts.Token);
        work.WorkId.ShouldBe("call_rw_sub");
        work.Title.ShouldBe("general");
        work.Label.ShouldBe("A long helper");
        work.CanStop.ShouldBeTrue();
        work.CanReadOutput.ShouldBeFalse();
        var child = await fleet.HarnessSessionAsync(work.ChildSessionId!, cts.Token);
        await WaitForAsync(events, async () => await child.GetActivityStatusAsync(cts.Token) == ActivityStatuses.Busy, cts.Token);

        var stopped = await WithOrchestratorAsync(o => o.StopWorkAsync(id, work.Id, cts.Token));

        stopped.IsSuccess.ShouldBeTrue(stopped.IsFailure ? stopped.Error.Description : null);
        stopped.Value.EndedReason.ShouldBe(WorkEndedReasons.Cancelled);
        await WaitForAsync(events, async () => await child.GetActivityStatusAsync(cts.Token) == ActivityStatuses.Idle, cts.Token);
        (await Delegations(id)).Single(d => d.ParentToolCallId == "call_rw_sub").Status.ShouldBe("cancelled");
    }

    private async Task<IReadOnlyList<RunningWorkItem>> Work(string sessionId)
    {
        using var user = BackgroundUserContext.BeginScope(OpenCode2LiveFleet.Owner);
        using var scope = fleet.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DelegationService>().GetWorkAsync(sessionId);
    }

    private async Task<T> WithOrchestratorAsync<T>(Func<SessionOrchestrator, Task<T>> act)
    {
        using var user = BackgroundUserContext.BeginScope(OpenCode2LiveFleet.Owner);
        using var scope = fleet.Services.CreateScope();
        return await act(scope.ServiceProvider.GetRequiredService<SessionOrchestrator>());
    }
}

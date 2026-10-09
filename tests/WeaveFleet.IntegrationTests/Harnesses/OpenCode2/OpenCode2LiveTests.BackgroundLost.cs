extern alias FakeLlm;

using System.Diagnostics;
using FakeLlm::FakeLlmServer;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Events;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;

namespace WeaveFleet.IntegrationTests.Harnesses.OpenCode2;

/// <summary>
/// V2 keeps background work in its server process. When the server dies under it (it crashed, or Fleet restarted),
/// the work dies too, and V2 never posts the notice that would end its call: the history keeps the call running for
/// good. Fleet ends it instead, live when the server stops and on every read after.
/// </summary>
public sealed partial class OpenCode2LiveTests
{
    [OpenCode2Fact]
    public async Task Background_work_the_server_died_under_ends_live_and_when_read_back()
    {
        const string shellPrompt = "Start the long one. (lost background shell)";
        const string subagentPrompt = "Hand it over. (lost background subagent)";
        const string childPrompt = "child-never-finishes";
        fleet.Answer(request =>
        {
            if (LlmRequest.Starts(request, childPrompt))
                return ToolCall("call_lost_child_work", "shell", new { command = "sleep 300; echo child-worked", description = "The child's work" });
            if (LlmRequest.Starts(request, shellPrompt))
                return ToolCall("call_lost_bg", "shell", new { command = "sleep 300; echo never-printed", description = "A job that outlives the server", background = true });
            if (LlmRequest.Continues(request, shellPrompt))
                return new ScriptedLlmResponse { Text = "It's running in the background." };
            if (LlmRequest.Starts(request, subagentPrompt))
                return ToolCall("call_lost_sub", "subagent", new { description = "A helper that outlives the server", prompt = childPrompt, agent = "general", subagent_type = "general", background = true });
            if (LlmRequest.Continues(request, subagentPrompt))
                return new ScriptedLlmResponse { Text = "The helper is on it." };
            return null;
        });

        using var cts = new CancellationTokenSource(Timeout);
        var id = await fleet.CreateSessionAsync(fleet.NewFolder("lost-background"), "Lost background", cts.Token);
        var events = fleet.Watch(cts.Token, id);

        await PromptAsync(id, shellPrompt, options: null, cts.Token);
        await WaitForAsync(events, () => events.For(id).Any(e => e.Type == "session.idle"), cts.Token);
        await PromptAsync(id, subagentPrompt, options: null, cts.Token);
        await WaitForAsync(events, () => events.For(id).Count(e => e.Type == "session.idle") >= 2, cts.Token);

        // Both calls returned and their work goes on: the delegation is open, its child in the background.
        var delegation = await WaitForAsync(events, async () => (await Delegations(id))
            .SingleOrDefault(d => d.ParentToolCallId == "call_lost_sub" && d.Status == "running" && d.ChildSessionId is not null), cts.Token);
        var tracker = fleet.Services.GetRequiredService<SessionActivityTracker>();
        await WaitForAsync(events, () => tracker.IsChildInBackground(delegation.ChildSessionId!), cts.Token);
        LatestParts<ToolMessageEventPart>(events, id).Where(p => p.CallId is "call_lost_bg" or "call_lost_sub")
            .Select(p => p.State.ShouldBeOfType<ToolRunningState>().Background).ShouldAllBe(background => background);

        // The server dies, and the work with it.
        var harness = (OpenCode2HarnessSession)await fleet.HarnessSessionAsync(id, cts.Token);
        using (var server = Process.GetProcessById(harness.ProcessId.ShouldNotBeNull()))
            server.Kill(entireProcessTree: true);

        // Live: both cards end, saying why, and the delegation ends cancelled, so nothing reads as still working.
        await WaitForAsync(events, () => LatestParts<ToolMessageEventPart>(events, id)
            .Count(p => p.CallId is "call_lost_bg" or "call_lost_sub" && IsLost(p)) == 2, cts.Token);
        await WaitForAsync(events, async () => (await Delegations(id)).Single(d => d.ParentToolCallId == "call_lost_sub").Status == "cancelled", cts.Token);
        tracker.IsChildInBackground(delegation.ChildSessionId!).ShouldBeFalse();

        // Read back, from a server that never ran the work, the history still says running; Fleet shows it ended.
        SessionSnapshot snapshot;
        using (var scope = fleet.Services.CreateScope())
        using (scope.ServiceProvider.GetRequiredService<IBackgroundUserScope>().Begin(OpenCode2LiveFleet.Owner))
            snapshot = await scope.ServiceProvider.GetRequiredService<ISessionMessageProxy>().GetSnapshotAsync(id, ct: cts.Token);

        var reopened = snapshot.Messages.SelectMany(m => m.Parts).OfType<ToolMessageEventPart>()
            .Where(p => p.CallId is "call_lost_bg" or "call_lost_sub")
            .ToList();
        reopened.Where(IsLost).Count().ShouldBe(2);
    }

    private static bool IsLost(ToolMessageEventPart part)
        => part.State is ToolErrorState { Error: OpenCode2Mapper.BackgroundWorkLost };
}

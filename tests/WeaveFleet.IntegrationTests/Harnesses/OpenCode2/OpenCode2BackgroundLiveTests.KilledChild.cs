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
/// A server that dies under a running subagent child leaves one ended delegation, no report, one card for the child's
/// shell and no detached shell daemon (<c>opencode-pty</c>) behind. Seen live on 2.3.0.
/// </summary>
public sealed partial class OpenCode2BackgroundLiveTests
{
    [OpenCode2Fact]
    public async Task A_server_that_dies_under_a_running_child_leaves_one_ended_delegation_and_no_shell_daemon()
    {
        const string prompt = "Hand it over, then the server dies. (killed child)";
        const string childPrompt = "child-killed-mid-turn";
        fleet.Answer(request =>
        {
            if (LlmRequest.Starts(request, childPrompt))
                return ToolCall("call_killed_child_shell", "shell", new { command = "sleep 300; echo never", description = "The child's work" });
            if (LlmRequest.Starts(request, prompt))
                return ToolCall("call_killed_sub", "subagent", new { description = "A helper that is killed", prompt = childPrompt, agent = "general", subagent_type = "general" });
            if (LlmRequest.Continues(request, prompt))
                return new ScriptedLlmResponse { Text = "Handed over." };
            return null;
        });

        using var cts = new CancellationTokenSource(Timeout);
        var id = await fleet.CreateSessionAsync(fleet.NewFolder("killed-child"), "Killed child", cts.Token);
        var events = fleet.Watch(cts.Token, id);
        await PromptAsync(id, prompt, options: null, cts.Token);
        var delegation = await WaitForAsync(events, async () => (await Delegations(id))
            .SingleOrDefault(d => d.ParentToolCallId == "call_killed_sub" && d.Status == "running" && d.ChildSessionId is not null), cts.Token);
        // The child's shell has started (its own session isn't watched here): the model was asked for it.
        await WaitForAsync(events, () => fleet.Llm.Queue.Requests.Any(r => LlmRequest.Starts(r, childPrompt)), cts.Token);
        await Task.Delay(4000, cts.Token);

        var harness = (OpenCode2HarnessSession)await fleet.HarnessSessionAsync(id, cts.Token);
        // The child's shell runs under a daemon this test's server started (others on the machine are someone else's).
        var ours = ShellDaemons(harness.ProcessId.ShouldNotBeNull());
        ours.ShouldNotBeEmpty("no shell daemon started: the check below would prove nothing");
        using (var server = Process.GetProcessById(harness.ProcessId.ShouldNotBeNull()))
            server.Kill(entireProcessTree: true);
        await WaitForAsync(events, async () => (await Delegations(id)).Single(d => d.ParentToolCallId == "call_killed_sub").Status is "cancelled" or "failed", cts.Token);
        await Task.Delay(5000, cts.Token);

        LatestParts<TextMessageEventPart>(events, id).Where(p => p.Text.StartsWith("<subagent", StringComparison.Ordinal)).ShouldBeEmpty();
        LatestParts<ToolMessageEventPart>(events, id).Count(p => p.CallId == "call_killed_sub").ShouldBe(1);
        (await Delegations(id)).Count(d => d.ParentToolCallId == "call_killed_sub").ShouldBe(1);
        SessionSnapshot snapshot;
        using (var scope = fleet.Services.CreateScope())
        using (scope.ServiceProvider.GetRequiredService<IBackgroundUserScope>().Begin(OpenCode2LiveFleet.Owner))
            snapshot = await scope.ServiceProvider.GetRequiredService<ISessionMessageProxy>().GetSnapshotAsync(delegation.ChildSessionId!, ct: cts.Token);
        var child = snapshot.Messages.SelectMany(m => m.Parts).OfType<ToolMessageEventPart>().Where(p => p.CallId == "call_killed_child_shell").ToList();
        child.Count.ShouldBe(1, "one card for the child's shell");
        child[0].State.ShouldNotBeOfType<ToolRunningState>();

        // The shell's detached daemon (opencode-pty) outlived the server: V2 never hears the server died, so Fleet must not leave it.
        await Task.Delay(3000, cts.Token);
        ShellDaemons(null).Intersect(ours).ShouldBeEmpty("a shell daemon outlived the server");
    }

    /// <summary>The <c>opencode-pty</c> daemons, those whose parent is <paramref name="parent"/> when given.</summary>
    private static HashSet<int> ShellDaemons(int? parent)
        => [.. Process.GetProcesses().Where(p =>
        {
            try
            {
                if (!p.ProcessName.Contains("opencode-pty", StringComparison.Ordinal))
                    return false;
                if (parent is null)
                    return true;
                // /proc/<pid>/stat: "pid (comm) state ppid ...".
                var stat = File.ReadAllText($"/proc/{p.Id}/stat");
                return int.Parse(stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture) == parent;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                return false;
            }
        }).Select(p => p.Id)];
}

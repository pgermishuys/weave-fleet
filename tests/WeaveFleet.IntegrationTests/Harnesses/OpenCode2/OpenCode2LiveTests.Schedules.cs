extern alias FakeLlm;

using System.Net.Http.Json;
using System.Reflection;
using FakeLlm::FakeLlmServer;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;
using WeaveFleet.Infrastructure.Sessions;

namespace WeaveFleet.IntegrationTests.Harnesses.OpenCode2;

/// <summary>
/// OpenCode 2 <c>experimental.schedules</c> (2.2.1): a schedule wakes its session with a prompt Fleet never sent, a
/// <c>user</c> item in V2's inbox under an id of V2's own (<c>msg_&lt;schedule&gt;_&lt;run&gt;</c>). V2 2.2.1 doesn't fire
/// schedules from <c>opencode serve</c> (a bare server doesn't either, checked 29 Oct 2026), so this sends the same prompt
/// the way a fire does (<c>sessions.prompt</c> with a message id, steer delivery, schedule metadata), from outside Fleet.
/// </summary>
public sealed partial class OpenCode2LiveTests
{
    [OpenCode2Fact]
    public async Task A_prompt_that_wakes_the_session_from_outside_Fleet_shows_as_a_prompt_live_and_when_opened_again()
    {
        const string first = "Wait for the schedule. (wake: first)";
        const string woken = "Time to look again. (wake: woken)";
        fleet.Answer(request => LlmRequest.Starts(request, first) ? new ScriptedLlmResponse { Text = "Waiting." }
            : LlmRequest.Starts(request, woken) ? new ScriptedLlmResponse { Text = "Looked again." }
            : null);

        using var cts = new CancellationTokenSource(Timeout);
        var id = await fleet.CreateSessionAsync(fleet.NewFolder("wake"), "Wake", cts.Token);
        var events = fleet.Watch(cts.Token, id);
        int Idles() => events.For(id).Count(e => e.Type == "session.idle");

        await PromptAsync(id, first, options: null, cts.Token);
        await WaitForAsync(events, () => Idles() == 1, cts.Token);

        // The fire: a prompt under V2's own id, none of Fleet's making.
        var harness = (OpenCode2HarnessSession)await fleet.HarnessSessionAsync(id, cts.Token);
        var servers = (System.Collections.Concurrent.ConcurrentDictionary<OpenCode2ServerKey, OpenCode2Server>)
            typeof(OpenCode2Servers).GetField("_servers", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(typeof(OpenCode2HarnessRuntime).GetField("_servers", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(fleet.Runtime))!;
        var server = servers.Values.Single();
        var http = (HttpClient)typeof(OpenCode2HttpClient).GetField("<http>P", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server.Client)!;
        using var sent = await http.PostAsJsonAsync($"api/session/{harness.ResumeToken}/prompt", new
        {
            id = "msg_sch_probe_1",
            text = woken,
            delivery = "steer",
            metadata = new { schedule = new { id = "sch_probe", name = "look again", run = 1, dueAt = 1_793_255_918_503L, late = false } },
        }, cts.Token);
        sent.EnsureSuccessStatusCode();

        await WaitForAsync(events, () => Idles() == 2, cts.Token);
        fleet.Llm.Queue.Requests.ShouldContain(r => LlmRequest.Starts(r, woken));

        // Live: the status turned busy, and the prompt came as its text.
        events.For(id).Any(e => e.Type == "session.status" && IsBusy(e)).ShouldBeTrue();
        Parts<TextMessageEventPart>(events, id).ShouldContain(p => p.Text.Contains(woken, StringComparison.Ordinal),
            $"Fleet showed nothing of the woken prompt; it sent: {events.Describe()}");

        // Opened again.
        SessionSnapshot snapshot;
        using (var scope = fleet.Services.CreateScope())
        using (scope.ServiceProvider.GetRequiredService<IBackgroundUserScope>().Begin(OpenCode2LiveFleet.Owner))
            snapshot = await scope.ServiceProvider.GetRequiredService<ISessionMessageProxy>().GetSnapshotAsync(id, ct: cts.Token);

        var users = snapshot.Messages.Where(m => m.Info.Role == "user").ToList();
        users.Count.ShouldBe(2);
        users[1].Parts.OfType<TextMessageEventPart>().ShouldHaveSingleItem().Text.ShouldContain(woken);

        // In the order it happened: the prompt, then the reply to it.
        snapshot.Messages.Select(m => m.Info.Role).ShouldBe(["user", "assistant", "user", "assistant"],
            $"ids: {string.Join(", ", snapshot.Messages.Select(m => m.Info.Id))}");
    }
}

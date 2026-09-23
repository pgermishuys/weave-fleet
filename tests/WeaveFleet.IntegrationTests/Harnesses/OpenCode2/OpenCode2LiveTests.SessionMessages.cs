extern alias FakeLlm;

using FakeLlm::FakeLlmServer;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Services;
using WeaveFleet.Infrastructure.Services;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.IntegrationTests.Harnesses.OpenCode2;

/// <summary>
/// Cross-session messages between OpenCode 2 sessions: a session that asks to hear back is told once when the turn
/// answering its message ends. V2's replies don't name the prompt they answer; the adapter names it from the inbox, and
/// this fails if that stops tying a reply to its message.
/// </summary>
public sealed partial class OpenCode2LiveTests
{
    [OpenCode2Fact]
    public async Task A_session_that_asks_hears_back_once_per_message_it_sends()
    {
        const string ask = "Send the receiver a message. (messages: ask)";
        const string askAgain = "And another one. (messages: ask again)";
        const string update = "<fleet-session-update";
        using var cts = new CancellationTokenSource(Timeout);
        var folder = fleet.NewFolder("session-messages");

        await SetSessionMessagesAsync(true);
        try
        {
            var receiver = await fleet.CreateSessionAsync(folder, "Update documentation", cts.Token);
            var sender = await fleet.CreateSessionAsync(folder, "Asker", cts.Token);
            fleet.Answer(request => LlmRequest.Starts(request, ask)
                ? ToolCall("call_msg_1", "fleet_message", new { sessionId = receiver, text = "Please update the docs, round 1.", notifyWhenDone = true })
                : LlmRequest.Starts(request, askAgain)
                ? ToolCall("call_msg_2", "fleet_message", new { sessionId = receiver, text = "Please update the docs, round 2.", notifyWhenDone = true })
                : LlmRequest.Continues(request, ask) || LlmRequest.Continues(request, askAgain) ? new ScriptedLlmResponse { Text = "Sent." }
                : LlmRequest.Starts(request, "round 1.") ? new ScriptedLlmResponse { Text = "Round 1 is done." }
                : LlmRequest.Starts(request, "round 2.") ? new ScriptedLlmResponse { Text = "Round 2 is done." }
                : LlmRequest.Starts(request, update) ? new ScriptedLlmResponse { Text = "Thanks." }
                : null);
            var events = fleet.Watch(cts.Token, sender, receiver);
            List<string> Updates() => fleet.Llm.Queue.Requests.Where(r => LlmRequest.Starts(r, update)).Select(r => LlmRequest.LastUserText(r)!).ToList();

            await PromptAsync(sender, ask, options: null, cts.Token);
            await WaitForAsync(events, () => Updates().Count == 1, cts.Token);
            await PromptAsync(sender, askAgain, options: null, cts.Token);
            await WaitForAsync(events, () => Updates().Count == 2, cts.Token);
            await Task.Delay(3000, cts.Token);

            // One update per message, each with the reply to that message and no other.
            var updates = Updates();
            updates.Count.ShouldBe(2);
            updates[0].ShouldContain("Round 1 is done.");
            updates[0].ShouldNotContain("Round 2");
            updates[1].ShouldContain("Round 2 is done.");
            updates[1].ShouldNotContain("Round 1");
        }
        finally
        {
            await SetSessionMessagesAsync(false);
        }
    }

    /// <summary>Turns session messages on or off as Settings does; the owner's next session gets a server to match.</summary>
    private async Task SetSessionMessagesAsync(bool enabled)
    {
        using var user = BackgroundUserContext.BeginScope(OpenCode2LiveFleet.Owner);
        using var scope = fleet.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IUserPreferenceRepository>().SetAsync(SessionMessages.PreferenceKey, enabled ? "true" : "false");
    }
}

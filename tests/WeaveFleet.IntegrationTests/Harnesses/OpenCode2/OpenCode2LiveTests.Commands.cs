extern alias FakeLlm;

using FakeLlm::FakeLlmServer;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.IntegrationTests.Harnesses.OpenCode2;

/// <summary>
/// A slash command: V2 turns it into a user message that holds the command's whole template, under an id of its own
/// that its command route doesn't return. The adapter reads the id off V2's inbox, and the conversation shows
/// "/name arguments" there, live and when the session is opened again; this fails if that stops tying them up.
/// </summary>
public sealed partial class OpenCode2LiveTests
{
    [OpenCode2Fact]
    public async Task A_command_shows_as_it_was_sent_live_and_when_the_session_is_opened_again()
    {
        const string first = "What does this repo do? (commands: first)";
        const string expanded = "Tidy the code in src/auth without changing what it does. (commands: tidy)";
        fleet.Answer(request => LlmRequest.Starts(request, first) ? new ScriptedLlmResponse { Text = "Not much yet." }
            : LlmRequest.Starts(request, expanded) ? new ScriptedLlmResponse { Text = "Tidied." }
            : null);
        using var cts = new CancellationTokenSource(Timeout);
        var folder = fleet.NewFolder("commands");
        Directory.CreateDirectory(Path.Combine(folder, ".opencode", "commands"));
        await File.WriteAllTextAsync(
            Path.Combine(folder, ".opencode", "commands", "tidy.md"),
            "---\ndescription: Tidy up part of the code\n---\nTidy the code in $ARGUMENTS without changing what it does. (commands: tidy)\n",
            cts.Token);
        var id = await fleet.CreateSessionAsync(folder, "Commands", cts.Token);
        var events = fleet.Watch(cts.Token, id);
        int Idles() => events.For(id).Count(e => e.Type == "session.idle");

        // A conversation to send the command into.
        await PromptAsync(id, first, options: null, cts.Token);
        await WaitForAsync(events, () => Idles() == 1, cts.Token);

        var sent = await fleet.WithOrchestratorAsync(o => o.CommandSessionAsync(
            id, new CommandOptions { Command = "tidy", Arguments = "src/auth" }, cts.Token));
        sent.IsSuccess.ShouldBeTrue(sent.IsFailure ? sent.Error.Description : null);
        await WaitForAsync(events, () => Idles() == 2, cts.Token);

        // The model got the template, expanded.
        fleet.Llm.Queue.Requests.ShouldContain(r => LlmRequest.Starts(r, expanded));

        // Opened again: V2's message, which holds the expanded template, carries the command.
        SessionSnapshot snapshot;
        using (var scope = fleet.Services.CreateScope())
        using (scope.ServiceProvider.GetRequiredService<IBackgroundUserScope>().Begin(OpenCode2LiveFleet.Owner))
            snapshot = await scope.ServiceProvider.GetRequiredService<ISessionMessageProxy>().GetSnapshotAsync(id, ct: cts.Token);

        var reopened = snapshot.Messages.Where(m => m.Info.Role == "user").ToList();
        reopened.Select(m => m.Info.Command).ShouldBe([null, new SlashCommand("tidy", "src/auth")]);
        reopened[1].Parts.OfType<TextMessageEventPart>().ShouldHaveSingleItem().Text.ShouldContain(expanded);

        // Live: Fleet's own message, the command as sent, sorting after the prompt before it. (The prompt's own message
        // can go out before the watch is listening, so only the last one is certain.)
        var live = events.For(id)
            .Where(e => e.Type == EventTypes.MessageUpdated && e.Payload.GetProperty("info").GetProperty("role").GetString() == "user")
            .Last().Payload.GetProperty("info");
        live.GetProperty("command").GetProperty("name").GetString().ShouldBe("tidy");
        live.GetProperty("command").GetProperty("arguments").GetString().ShouldBe("src/auth");
        string.CompareOrdinal(live.GetProperty("id").GetString(), reopened[0].Info.Id).ShouldBeGreaterThan(0);
    }
}

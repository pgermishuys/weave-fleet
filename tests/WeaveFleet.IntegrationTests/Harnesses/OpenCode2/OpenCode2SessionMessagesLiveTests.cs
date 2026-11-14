extern alias FakeLlm;

using FakeLlm::FakeLlmServer;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Events;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Users;

namespace WeaveFleet.IntegrationTests.Harnesses.OpenCode2;

/// <summary>
/// One session's agent messages another on OpenCode 2, with a real <c>opencode2</c> and a scripted model: what the API
/// does with an agent's prompt with messages between sessions off and on, and which turn a session that asked to hear
/// back is told about. Ported from OpenCode 1's <c>SessionMessagesLiveTests</c>, which were the only cover for these.
/// Each test waits on what Fleet and the model saw, never on time.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Harness", "opencode2")]
public sealed class OpenCode2SessionMessagesLiveTests(OpenCode2LiveFleet fleet) : OpenCode2LiveTestBase, IClassFixture<OpenCode2LiveFleet>
{
    private const string Update = "<fleet-session-update";

    protected override OpenCode2LiveFleet Fleet => fleet;

    [OpenCode2Fact]
    public async Task With_messages_off_an_agent_that_prompts_another_session_through_the_API_speaks_as_the_user()
    {
        const string ask = "Tell the docs session that the login redirect changed. (messages off: api)";
        const string note = "The login redirect changed, round off. Please update the authentication docs.";
        using var cts = new CancellationTokenSource(Timeout);
        var folder = fleet.NewFolder("messages-off-api");

        await SetSessionMessagesAsync(false);
        var receiver = await fleet.CreateSessionAsync(folder, "Update documentation", cts.Token);
        var sender = await fleet.CreateSessionAsync(folder, "Fix authentication flow", cts.Token);
        fleet.Answer(request => LlmRequest.Starts(request, ask) ? ToolCall("call_curl_off", "shell", new { command = CurlPrompt(receiver, note), description = "Message the docs session" })
            : LlmRequest.Continues(request, ask) ? new ScriptedLlmResponse { Text = "Sent." }
            : LlmRequest.Starts(request, note) ? new ScriptedLlmResponse { Text = "Docs updated." }
            : null);
        var events = fleet.Watch(cts.Token, sender, receiver);

        await PromptAsync(sender, ask, options: null, cts.Token);
        await WaitForAsync(events, () => Requests().Any(r => LlmRequest.Continues(r, ask)) && Requests().Any(r => LlmRequest.Starts(r, note)), cts.Token);

        // No tool, and the API takes it.
        LlmRequest.OfferedToolNames(Requests().First(r => LlmRequest.Starts(r, ask))).ShouldNotContain("fleet_message");
        LlmRequest.LastToolText(Requests().First(r => LlmRequest.Continues(r, ask))).ShouldNotBeNull().ShouldContain("HTTP 200");

        // The receiver's model got the text as a plain user message: nothing says another session sent it.
        LlmRequest.LastUserText(Requests().First(r => LlmRequest.Starts(r, note))).ShouldBe(note);
        events.All.ShouldNotContain(e => e.Type == "session.messaged");
    }

    [OpenCode2Fact]
    public async Task With_messages_on_the_API_refuses_an_agents_prompt_and_names_the_tool()
    {
        const string ask = "Tell the docs session that the login redirect changed. (messages on: api)";
        const string note = "The login redirect changed, round on. Please update the authentication docs.";
        using var cts = new CancellationTokenSource(Timeout);
        var folder = fleet.NewFolder("messages-on-api");

        await SetSessionMessagesAsync(true);
        try
        {
            var receiver = await fleet.CreateSessionAsync(folder, "Update documentation", cts.Token);
            var sender = await fleet.CreateSessionAsync(folder, "Fix authentication flow", cts.Token);
            fleet.Answer(request => LlmRequest.Starts(request, ask) ? ToolCall("call_curl_on", "shell", new { command = CurlPrompt(receiver, note), description = "Message the docs session" })
                : LlmRequest.Continues(request, ask) ? new ScriptedLlmResponse { Text = "Refused." }
                : null);
            var events = fleet.Watch(cts.Token, sender, receiver);

            await PromptAsync(sender, ask, options: null, cts.Token);
            await WaitForAsync(events, () => Requests().Any(r => LlmRequest.Continues(r, ask)), cts.Token);

            // The API answers before anything reaches the receiver, so by the time the model has the answer, nothing did.
            var result = LlmRequest.LastToolText(Requests().First(r => LlmRequest.Continues(r, ask))).ShouldNotBeNull();
            result.ShouldContain("HTTP 409");
            result.ShouldContain("Use the fleet_message tool to message a session.");
            Requests().ShouldNotContain(r => r.Contains(note) && !LlmRequest.Asked(r, ask));
            events.For(receiver).ShouldNotContain(e => IsBusy(e));
        }
        finally
        {
            await SetSessionMessagesAsync(false);
        }
    }

    [OpenCode2Fact]
    public async Task A_turn_the_other_session_was_already_running_does_not_count()
    {
        const string slow = "Run the slow link check. (messages: busy)";
        const string slowReply = "The link check passed.";
        const string ask = "Tell the docs session that the login redirect changed. (messages: busy)";
        const string note = "The login redirect changed, busy round. Please update the authentication docs.";
        const string reply = "Docs updated: the redirect section, busy round.";
        using var cts = new CancellationTokenSource(Timeout);
        // The receiver's check holds its turn open until the sender's message has reached it, so the message always
        // arrives mid-turn.
        using var delivered = new ManualResetEventSlim();
        var folder = fleet.NewFolder("messages-busy");

        await SetSessionMessagesAsync(true);
        try
        {
            var receiver = await fleet.CreateSessionAsync(folder, "Update documentation", cts.Token);
            var sender = await fleet.CreateSessionAsync(folder, "Fix authentication flow", cts.Token);
            fleet.Answer(request => LlmRequest.Starts(request, slow) ? ToolCall("call_slow", "shell", new { command = "echo checked", description = "Check the links" })
                : LlmRequest.Continues(request, slow) && !request.Contains(note, StringComparison.Ordinal)
                    ? delivered.Wait(TimeSpan.FromSeconds(60)) ? new ScriptedLlmResponse { Text = slowReply } : new ScriptedLlmResponse { Text = "The message never came." }
                : LlmRequest.Starts(request, ask) ? ToolCall("call_msg_busy", "fleet_message", new { sessionId = receiver, text = note, notifyWhenDone = true })
                : LlmRequest.Continues(request, ask) ? new ScriptedLlmResponse { Text = "Sent." }
                : LlmRequest.Starts(request, Update) && IsAbout(request, receiver) ? new ScriptedLlmResponse { Text = "Thanks." }
                // The message, as the turn's latest prompt: the sender's own requests have it too, in its tool call.
                : LlmRequest.Starts(request, note) ? new ScriptedLlmResponse { Text = reply }
                : null);
            var events = fleet.Watch(cts.Token, sender, receiver);

            // Through the orchestrator, as the user would, so Fleet sees the receiver's turn start.
            await PromptAsync(receiver, slow, options: null, cts.Token);
            await WaitForAsync(events, () => Requests().Any(r => LlmRequest.Continues(r, slow)), cts.Token);
            await PromptAsync(sender, ask, options: null, cts.Token);
            await WaitForAsync(events, () => Requests().Any(r => LlmRequest.Continues(r, ask)), cts.Token);
            LlmRequest.LastToolText(Requests().First(r => LlmRequest.Continues(r, ask))).ShouldNotBeNull().ShouldContain("Delivered to Update documentation");
            delivered.Set();

            // The receiver answered the message, Fleet told the sender, and the sender's turn for it is over.
            await WaitForAsync(events, () => Updates(receiver).Count >= 1
                && LatestParts<TextMessageEventPart>(events, sender).Any(p => p.Text == "Thanks.")
                && events.For(sender).Any(e => e.Type == "session.reported"), cts.Token);

            // The message was handled in the turn that was running, after the check's result: told once, about that.
            Requests().ShouldContain(r => LlmRequest.Asked(r, note) && r.Contains("call_slow", StringComparison.Ordinal));
            var update = Updates(receiver).ShouldHaveSingleItem();
            update.ShouldContain(reply);
            update.ShouldNotContain(slowReply);
            events.For(sender).Where(e => e.Type == "session.reported").ShouldHaveSingleItem();
        }
        finally
        {
            delivered.Set();
            await SetSessionMessagesAsync(false);
        }
    }

    [OpenCode2Fact]
    public async Task A_turn_an_update_started_cannot_ask_to_be_told_again()
    {
        const string ask = "Tell the docs session that the login redirect changed. (messages: again)";
        const string note = "The login redirect changed, again round. Please update the authentication docs.";
        const string reply = "Docs updated: the redirect section, again round.";
        const string followUp = "Thanks. Now update the changelog too, again round.";
        const string refused = "can't ask to be told again";
        using var cts = new CancellationTokenSource(Timeout);
        var folder = fleet.NewFolder("messages-again");

        await SetSessionMessagesAsync(true);
        try
        {
            var receiver = await fleet.CreateSessionAsync(folder, "Update documentation", cts.Token);
            var sender = await fleet.CreateSessionAsync(folder, "Fix authentication flow", cts.Token);
            fleet.Answer(request => LlmRequest.Starts(request, ask) ? ToolCall("call_msg_again", "fleet_message", new { sessionId = receiver, text = note, notifyWhenDone = true })
                : LlmRequest.Continues(request, ask) ? new ScriptedLlmResponse { Text = "Sent." }
                : LlmRequest.Starts(request, note) ? new ScriptedLlmResponse { Text = reply }
                // Woken by the update, the sender tries to keep the loop going.
                : LlmRequest.Starts(request, Update) && IsAbout(request, receiver)
                    ? ToolCall("call_again", "fleet_message", new { sessionId = receiver, text = followUp, notifyWhenDone = true })
                : LlmRequest.Continues(request, Update) && IsAbout(request, receiver) ? new ScriptedLlmResponse { Text = "Understood." }
                : null);
            var events = fleet.Watch(cts.Token, sender, receiver);

            await PromptAsync(sender, ask, options: null, cts.Token);
            await WaitForAsync(events, () => Requests().Any(r => LlmRequest.LastToolText(r) is { } text && text.Contains(refused, StringComparison.Ordinal)), cts.Token);

            LlmRequest.LastToolText(Requests().First(r => LlmRequest.LastToolText(r) is { } text && text.Contains(refused, StringComparison.Ordinal)))
                .ShouldNotBeNull().ShouldContain("Send it with notifyWhenDone false.");

            // Refused outright, before anything was sent: the receiver never got the follow-up, and there was one
            // message and one report.
            Requests().ShouldNotContain(r => LlmRequest.Starts(r, followUp));
            events.All.Where(e => e.Type == "session.messaged").ShouldHaveSingleItem();
            events.All.Where(e => e.Type == "session.reported").ShouldHaveSingleItem();
        }
        finally
        {
            await SetSessionMessagesAsync(false);
        }
    }

    private List<string> Requests() => [.. fleet.Llm.Queue.Requests];

    /// <summary>The updates Fleet sent about <paramref name="receiver"/>: the prompt of each turn one started.</summary>
    private List<string> Updates(string receiver)
        => Requests().Where(r => LlmRequest.Starts(r, Update) && LlmRequest.LastUserText(r)!.Contains(From(receiver), StringComparison.Ordinal))
            .Select(r => LlmRequest.LastUserText(r)!).ToList();

    /// <summary>Whether the turn's prompt is an update about <paramref name="receiver"/>.</summary>
    private static bool IsAbout(string request, string receiver)
        => LlmRequest.LastUserText(request) is { } text && text.Contains(From(receiver), StringComparison.Ordinal);

    /// <summary>How an update names the session it's about.</summary>
    private static string From(string sessionId) => $"session=\"{sessionId}\"";

    /// <summary>A shell command that prompts <paramref name="sessionId"/> through Fleet's API, as the agent's own call.</summary>
    private static string CurlPrompt(string sessionId, string text)
        => $$"""curl -s -w '\nHTTP %{http_code}' -X POST "$FLEET_URL/api/sessions/{{sessionId}}/prompt" -H 'content-type: application/json' -d '{"text":"{{text}}"}'""";

    /// <summary>Turns session messages on or off as Settings does; the owner's next session gets a server to match.</summary>
    private async Task SetSessionMessagesAsync(bool enabled)
    {
        using var user = BackgroundUserContext.BeginScope(OpenCode2LiveFleet.Owner);
        using var scope = fleet.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IUserPreferenceRepository>().SetAsync(SessionMessages.PreferenceKey, enabled ? "true" : "false");
    }
}

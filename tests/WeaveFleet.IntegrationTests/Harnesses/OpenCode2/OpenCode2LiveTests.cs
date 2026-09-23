extern alias FakeLlm;

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FakeLlm::FakeLlmServer;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Canvases;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure;
using WeaveFleet.Infrastructure.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.IntegrationTests.Harnesses.OpenCode2;

/// <summary>
/// OpenCode 2 sessions end to end, with a real <c>opencode2</c> and a scripted model: what a user notices first. Each
/// test drives Fleet the way the client does (the orchestrator creates, prompts and answers) and checks what Fleet
/// broadcast, what the model was asked, and what Fleet shows when the session is opened again. See
/// <see cref="OpenCode2LiveFleet"/> for the scratch setup.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Harness", "opencode2")]
public sealed partial class OpenCode2LiveTests(OpenCode2LiveFleet fleet) : IClassFixture<OpenCode2LiveFleet>
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    [OpenCode2Fact]
    public async Task A_prompt_streams_the_reply_and_the_turn_ends()
    {
        const string prompt = "Say hello. (text turn)";
        const string reply = "Hello from the scripted model, streamed in a few chunks.";
        fleet.Answer(request => LlmRequest.Starts(request, prompt) ? new ScriptedLlmResponse { Text = reply } : null);

        using var cts = new CancellationTokenSource(Timeout);
        var id = await fleet.CreateSessionAsync(fleet.NewFolder("text"), "Text turn", cts.Token);
        var events = fleet.Watch(cts.Token, id);

        await PromptAsync(id, prompt, options: null, cts.Token);
        await WaitForAsync(events, () => events.For(id).Any(e => e.Type == "session.idle"), cts.Token);

        // Working, then idle, without a failure: what the session list and the header show.
        var sent = events.For(id).ToList();
        var busy = sent.FindIndex(IsBusy);
        busy.ShouldBeGreaterThanOrEqualTo(0, "Fleet never said the session was working.");
        busy.ShouldBeLessThan(sent.FindIndex(e => e.Type == "session.idle"));
        sent.ShouldNotContain(e => e.Type == "session.error");
        LatestParts<TextMessageEventPart>(events, id).ShouldHaveSingleItem().Text.ShouldBe(reply);

        var assistant = Messages(events, id).Last(m => m.Info.Role == "assistant").Info;
        assistant.ModelId.ShouldBe("fake-model");
        assistant.Tokens.ShouldNotBeNull();
    }

    [OpenCode2Fact]
    public async Task A_tool_call_shows_as_a_card_while_it_runs_and_with_its_output_when_done()
    {
        const string prompt = "Run echo for me. (tool turn)";
        fleet.Answer(request => LlmRequest.Starts(request, prompt) ? ToolCall("call_echo", "shell", new { command = "echo from-the-tool", description = "Echo a word" })
            : LlmRequest.Continues(request, prompt) ? new ScriptedLlmResponse { Text = "It printed from-the-tool." }
            : null);

        using var cts = new CancellationTokenSource(Timeout);
        var id = await fleet.CreateSessionAsync(fleet.NewFolder("tool"), "Tool turn", cts.Token);
        var events = fleet.Watch(cts.Token, id);

        await PromptAsync(id, prompt, options: null, cts.Token);
        await WaitForAsync(events, () => events.For(id).Any(e => e.Type == "session.idle"), cts.Token);

        var card = Parts<ToolMessageEventPart>(events, id).Where(p => p.CallId == "call_echo").ToList();
        card.ShouldNotBeEmpty();
        card.ShouldAllBe(p => p.ToolName == "shell");
        card.ShouldContain(p => p.State is ToolRunningState, "The card never showed the call running.");
        var done = card[^1].State.ShouldBeOfType<ToolCompletedState>();
        done.Output.ShouldNotBeNull().GetString().ShouldNotBeNull().ShouldContain("from-the-tool");
        JsonSerializer.Serialize(done.Input).ShouldContain("echo from-the-tool");

        // The model got the tool's output and the reply followed the card.
        fleet.Llm.Queue.Requests.Where(r => LlmRequest.Continues(r, prompt)).ShouldHaveSingleItem()
            .ShouldContain("from-the-tool");
        LatestParts<TextMessageEventPart>(events, id).Last().Text.ShouldBe("It printed from-the-tool.");
    }

    [OpenCode2Fact]
    public async Task A_question_waits_for_the_user_and_the_answer_goes_back_to_the_model()
    {
        const string prompt = "Which one should I pick? Ask me. (question)";
        fleet.Answer(request => LlmRequest.Starts(request, prompt) ? ToolCall("call_pick", "question", Question("Pick one"))
            : LlmRequest.Continues(request, prompt) ? new ScriptedLlmResponse { Text = "You picked B." }
            : null);

        using var cts = new CancellationTokenSource(Timeout);
        var id = await fleet.CreateSessionAsync(fleet.NewFolder("question"), "Question", cts.Token);
        var events = fleet.Watch(cts.Token, id);

        await PromptAsync(id, prompt, options: null, cts.Token);

        // The question card shows, and the session needs the user.
        await WaitForAsync(events, () => Parts<ToolMessageEventPart>(events, id).Any(p => p.CallId == "call_pick" && p.State is ToolRunningState), cts.Token);
        var harness = await fleet.HarnessSessionAsync(id, cts.Token);
        await WaitForAsync(events, async () => await harness.GetActivityStatusAsync(cts.Token) == ActivityStatuses.WaitingInput, cts.Token);
        events.For(id).Select(e => e.Type).ShouldNotContain("session.idle");

        // Answered from the card: by the tool call's id, with the chosen label.
        var answered = await fleet.WithOrchestratorAsync(o => o.AnswerQuestionAsync(id, "call_pick", [["B"]], cts.Token));
        answered.IsSuccess.ShouldBeTrue(answered.IsFailure ? answered.Error.Description : null);
        await WaitForAsync(events, () => events.For(id).Any(e => e.Type == "session.idle"), cts.Token);

        LlmRequest.LastToolText(fleet.Llm.Queue.Requests.Single(r => LlmRequest.Continues(r, prompt))).ShouldNotBeNull().ShouldContain("B");
        var card = LatestParts<ToolMessageEventPart>(events, id).Single(p => p.CallId == "call_pick");
        card.ToolName.ShouldBe("question");
        card.State.ShouldBeOfType<ToolCompletedState>();
        (await harness.GetActivityStatusAsync(cts.Token)).ShouldBe(ActivityStatuses.Idle);
    }

    [OpenCode2Fact]
    public async Task A_reopened_session_shows_every_part_as_the_live_stream_left_it()
    {
        const string prompt = "Check the folder, ask me, then sum up. (reopen)";
        fleet.Answer(request =>
        {
            if (LlmRequest.Starts(request, prompt))
                return ToolCall("call_ls", "shell", new { command = "echo one two", description = "List the words" });
            if (!LlmRequest.Continues(request, prompt))
                return null;
            return LlmRequest.LastToolText(request) is { } result && result.Contains("one two", StringComparison.Ordinal)
                ? ToolCall("call_ask", "question", Question("Keep going?"))
                : new ScriptedLlmResponse { Text = "Summed up: one two, and you said B." };
        });

        using var cts = new CancellationTokenSource(Timeout);
        var id = await fleet.CreateSessionAsync(fleet.NewFolder("reopen"), "Reopen", cts.Token);
        var events = fleet.Watch(cts.Token, id);

        await PromptAsync(id, prompt, options: null, cts.Token);
        await WaitForAsync(events, () => Parts<ToolMessageEventPart>(events, id).Any(p => p.CallId == "call_ask" && p.State is ToolRunningState), cts.Token);
        (await fleet.WithOrchestratorAsync(o => o.AnswerQuestionAsync(id, "call_ask", [["B"]], cts.Token))).IsSuccess.ShouldBeTrue();
        await WaitForAsync(events, () => events.For(id).Any(e => e.Type == "session.idle"), cts.Token);

        // Navigating back: the client subscribes and gets this snapshot, read from V2's history.
        SessionSnapshot snapshot;
        using (var scope = fleet.Services.CreateScope())
        using (scope.ServiceProvider.GetRequiredService<IBackgroundUserScope>().Begin(OpenCode2LiveFleet.Owner))
            snapshot = await scope.ServiceProvider.GetRequiredService<ISessionMessageProxy>().GetSnapshotAsync(id, ct: cts.Token);

        snapshot.IsPartial.ShouldBeFalse();
        var reopened = snapshot.Messages.Where(m => m.Info.Role == "assistant").SelectMany(m => m.Parts)
            .Where(p => p is TextMessageEventPart or ToolMessageEventPart)
            .ToList();
        reopened.OfType<ToolMessageEventPart>().Select(p => p.CallId).ShouldBe(["call_ls", "call_ask"]);
        reopened.OfType<TextMessageEventPart>().Select(p => p.Text).ShouldContain("Summed up: one two, and you said B.");

        // Each part comes back as the live stream last showed it, under the same id.
        var live = events.For(id).Select(e => e.DomainEvent).OfType<MessagePartUpdated>().Select(e => e.Payload.Part)
            .GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.Last());
        foreach (var part in reopened)
        {
            live.ShouldContainKey(part.Id, $"The live stream never sent part {part.Id} ({part.GetType().Name}).");
            JsonNode.DeepEquals(ToJson(id, part), ToJson(id, live[part.Id]))
                .ShouldBeTrue($"Part {part.Id} differs.\nReopened: {ToJson(id, part)}\nLive:     {ToJson(id, live[part.Id])}");
        }

        // The prompt is shown under the id Fleet showed it with.
        snapshot.Messages.Where(m => m.Info.Role == "user").SelectMany(m => m.Parts).OfType<TextMessageEventPart>()
            .Select(p => p.Text).ShouldContain(prompt);
    }

    [OpenCode2Fact]
    public async Task The_agent_opens_a_canvas_with_Fleets_tool()
    {
        const string prompt = "Draw the flow. (canvas)";
        fleet.Answer(request => LlmRequest.Starts(request, prompt)
            ? new ScriptedLlmResponse
            {
                StopReason = "tool_calls",
                ToolCalls =
                [
                    new ScriptedToolCall("call_open", "fleet_canvas_open", """
                        {"kind":"diagram","title":"Session flow","state":{"nodes":[{"id":"n1","label":"Client"},{"id":"n2","label":"Fleet"}],"edges":[{"id":"e1","from":"n1","to":"n2","label":"prompts"}]}}
                        """),
                ],
            }
            : LlmRequest.Continues(request, prompt) ? new ScriptedLlmResponse { Text = "Drawn." }
            : null);

        using var cts = new CancellationTokenSource(Timeout);
        var id = await fleet.CreateSessionAsync(fleet.NewFolder("canvas"), "Canvas", cts.Token);
        var events = fleet.Watch(cts.Token, id);

        await PromptAsync(id, prompt, options: null, cts.Token);
        await WaitForAsync(events, () => events.For(id).Any(e => e.Type == "session.idle"), cts.Token);

        var opened = events.For(id).Where(e => e.Type == "canvas.updated").ShouldHaveSingleItem();
        opened.Payload.GetProperty("version").GetInt32().ShouldBe(1);
        opened.Payload.GetProperty("actor").GetString().ShouldBe("agent");

        using (var scope = fleet.Services.CreateScope())
        using (scope.ServiceProvider.GetRequiredService<IBackgroundUserScope>().Begin(OpenCode2LiveFleet.Owner))
        {
            var canvas = (await scope.ServiceProvider.GetRequiredService<ICanvasService>().ListAsync(id, cts.Token)).ShouldHaveSingleItem().Canvas;
            canvas.UserId.ShouldBe(OpenCode2LiveFleet.Owner);
            DiagramState.Parse(canvas.StateJson).Nodes.Select(n => n.Id).ShouldBe(["n1", "n2"]);
        }

        // The tool's result told the model the canvas it opened.
        LlmRequest.LastToolText(fleet.Llm.Queue.Requests.Single(r => LlmRequest.Continues(r, prompt))).ShouldNotBeNull()
            .ShouldMatch(CanvasId().ToString());
        LatestParts<ToolMessageEventPart>(events, id).Single(p => p.CallId == "call_open").State.ShouldBeOfType<ToolCompletedState>();
    }

    [OpenCode2Fact]
    public async Task A_subagent_runs_as_a_child_session_whose_question_the_user_answers()
    {
        const string prompt = "Hand the database choice to a helper. (subagent)";
        const string childPrompt = "Ask the user which database to use.";
        fleet.Answer(request =>
        {
            // The child's conversation first: V2 hands the child the parent's context too.
            if (LlmRequest.Starts(request, childPrompt))
                return ToolCall("call_child_ask", "question", Question("Which database?"));
            if (LlmRequest.Continues(request, childPrompt))
                return new ScriptedLlmResponse { Text = "B it is." };
            if (LlmRequest.Starts(request, prompt))
                return ToolCall("call_sub", "subagent", new { description = "Pick a database", prompt = childPrompt, agent = "general", subagent_type = "general" });
            if (LlmRequest.Continues(request, prompt))
                return new ScriptedLlmResponse { Text = "The helper says: B." };
            return null;
        });

        using var cts = new CancellationTokenSource(Timeout);
        var id = await fleet.CreateSessionAsync(fleet.NewFolder("subagent"), "Subagent", cts.Token);
        var events = fleet.Watch(cts.Token, id);

        await PromptAsync(id, prompt, options: null, cts.Token);

        // The call is a delegation whose child is a Fleet session of its own.
        var childId = await WaitForAsync(events, async () =>
            (await Delegations(id)).FirstOrDefault(d => d.ParentToolCallId == "call_sub")?.ChildSessionId, cts.Token);
        var child = await fleet.HarnessSessionAsync(childId, cts.Token);
        child.HarnessType.ShouldBe(OpenCode2HarnessSession.Type);

        // The child's question reaches its Fleet session, which needs the user; the parent is still at work.
        await WaitForAsync(events, async () => await child.GetActivityStatusAsync(cts.Token) == ActivityStatuses.WaitingInput, cts.Token);
        events.For(id).Select(e => e.Type).ShouldNotContain("session.idle");
        (await Delegations(id)).Single(d => d.ParentToolCallId == "call_sub").Status.ShouldBe("running");

        var answered = await fleet.WithOrchestratorAsync(o => o.AnswerQuestionAsync(childId, "call_child_ask", [["B"]], cts.Token));
        answered.IsSuccess.ShouldBeTrue(answered.IsFailure ? answered.Error.Description : null);

        // The child finishes, then the parent, with the child's answer.
        await WaitForAsync(events, () => events.For(id).Any(e => e.Type == "session.idle"), cts.Token);
        await WaitForAsync(events, async () => (await Delegations(id)).Single(d => d.ParentToolCallId == "call_sub").Status == "completed", cts.Token);
        LlmRequest.LastToolText(fleet.Llm.Queue.Requests.Single(r => LlmRequest.Continues(r, childPrompt))).ShouldNotBeNull().ShouldContain("B");
        LlmRequest.LastToolText(fleet.Llm.Queue.Requests.Single(r => LlmRequest.Continues(r, prompt))).ShouldNotBeNull().ShouldContain("B it is.");
        LatestParts<TextMessageEventPart>(events, id).Last().Text.ShouldBe("The helper says: B.");
        (await child.GetActivityStatusAsync(cts.Token)).ShouldBe(ActivityStatuses.Idle);
    }

    [OpenCode2Fact]
    public async Task A_backgrounded_shell_keeps_its_card_running_until_the_notice_that_it_finished()
    {
        const string prompt = "Start the long one and carry on. (background shell)";
        fleet.Answer(request =>
        {
            if (LlmRequest.Starts(request, prompt))
                return ToolCall("call_bg", "shell", new { command = "sleep 3; echo late-output", description = "A slow job", background = true });
            if (LlmRequest.Continues(request, prompt))
                return new ScriptedLlmResponse { Text = "It's running in the background." };
            // V2 wakes the session with a notice of its own; the model is called again with it.
            if (LlmRequest.Starts(request, "<shell"))
                return new ScriptedLlmResponse { Text = "The background command finished." };
            return null;
        });

        using var cts = new CancellationTokenSource(Timeout);
        var id = await fleet.CreateSessionAsync(fleet.NewFolder("background-shell"), "Background shell", cts.Token);
        var events = fleet.Watch(cts.Token, id);

        await PromptAsync(id, prompt, options: null, cts.Token);
        await WaitForAsync(events, () => events.For(id).Any(e => e.Type == "session.idle"), cts.Token);

        // The turn ended, but the call's card is still running: it says where the work went.
        var card = LatestParts<ToolMessageEventPart>(events, id).Single(p => p.CallId == "call_bg");
        var running = card.State.ShouldBeOfType<ToolRunningState>();
        running.Output.ShouldNotBeNull().GetString().ShouldNotBeNull().ShouldContain("moved to the background");
        running.Metadata.ShouldNotBeNull().GetProperty("shellID").GetString().ShouldNotBeNullOrEmpty();

        // The notice V2 posts when the command really finishes shows in the conversation, with its output.
        var notice = await WaitForAsync(events, () => Task.FromResult(LatestParts<TextMessageEventPart>(events, id)
            .FirstOrDefault(p => p.Text.StartsWith("<shell", StringComparison.Ordinal))), cts.Token);
        notice.Text.ShouldContain("late-output");
        notice.Text.ShouldContain("state=\"completed\"");
        // The notice is the harness's own word, so it's neither the user's message nor a turn of the agent's.
        Messages(events, id).Last(m => m.Info.Id == notice.MessageId).Info.Role.ShouldBe("notice");

        // And the session picks up by itself, with the notice in front of the model.
        await WaitForAsync(events, () => LatestParts<TextMessageEventPart>(events, id).Any(p => p.Text == "The background command finished."), cts.Token);

        // Read back from V2's history, the session shows the same: V2 never updates the call it backgrounded, so the
        // card is still running there, and the notice is still the message that says the work is done.
        SessionSnapshot snapshot;
        using (var scope = fleet.Services.CreateScope())
        using (scope.ServiceProvider.GetRequiredService<IBackgroundUserScope>().Begin(OpenCode2LiveFleet.Owner))
            snapshot = await scope.ServiceProvider.GetRequiredService<ISessionMessageProxy>().GetSnapshotAsync(id, ct: cts.Token);

        var reopened = snapshot.Messages.SelectMany(m => m.Parts).OfType<ToolMessageEventPart>().Single(p => p.CallId == "call_bg");
        reopened.State.ShouldBeOfType<ToolRunningState>().Background.ShouldBeTrue();
        var reopenedNotice = snapshot.Messages.Single(m => m.Info.Id == notice.MessageId);
        reopenedNotice.Info.Role.ShouldBe("notice");
        reopenedNotice.Parts.OfType<TextMessageEventPart>().ShouldHaveSingleItem().Text.ShouldBe(notice.Text);
    }

    [OpenCode2Fact]
    public async Task A_background_subagents_delegation_stays_open_while_its_child_works()
    {
        const string prompt = "Hand it over and carry on. (background subagent)";
        const string childPrompt = "child-takes-a-while";
        fleet.Answer(request =>
        {
            if (LlmRequest.Starts(request, childPrompt))
                return ToolCall("call_child_work", "shell", new { command = "sleep 4; echo child-worked", description = "The child's work" });
            if (LlmRequest.Continues(request, childPrompt))
                return new ScriptedLlmResponse { Text = "The child is done." };
            if (LlmRequest.Starts(request, prompt))
                return ToolCall("call_bg_sub", "subagent", new { description = "A slow helper", prompt = childPrompt, agent = "general", subagent_type = "general", background = true });
            if (LlmRequest.Continues(request, prompt))
                return new ScriptedLlmResponse { Text = "The helper is on it." };
            if (LlmRequest.Starts(request, "<subagent"))
                return new ScriptedLlmResponse { Text = "The helper came back." };
            return null;
        });

        using var cts = new CancellationTokenSource(Timeout);
        var id = await fleet.CreateSessionAsync(fleet.NewFolder("background-subagent"), "Background subagent", cts.Token);
        var events = fleet.Watch(cts.Token, id);

        await PromptAsync(id, prompt, options: null, cts.Token);
        await WaitForAsync(events, () => events.For(id).Any(e => e.Type == "session.idle"), cts.Token);

        // The parent's turn is over while the child is still working: the delegation is still open, and its card too.
        // Fleet records the call and links its child in the background, so it reads "pending" for a moment first.
        var delegation = await WaitForAsync(events, async () => (await Delegations(id))
            .SingleOrDefault(d => d.ParentToolCallId == "call_bg_sub" && d.Status != "pending"), cts.Token);
        delegation.Status.ShouldBe("running");
        delegation.ChildSessionId.ShouldNotBeNull();
        var child = await fleet.HarnessSessionAsync(delegation.ChildSessionId!, cts.Token);
        LatestParts<ToolMessageEventPart>(events, id).Single(p => p.CallId == "call_bg_sub").State.ShouldBeOfType<ToolRunningState>();

        // The child finishes, the notice says so, and only then is the delegation done.
        await WaitForAsync(events, async () => (await Delegations(id)).Single(d => d.ParentToolCallId == "call_bg_sub").Status == "completed", cts.Token);
        (await child.GetActivityStatusAsync(cts.Token)).ShouldBe(ActivityStatuses.Idle);
        LatestParts<TextMessageEventPart>(events, id)
            .ShouldContain(p => p.Text.StartsWith("<subagent", StringComparison.Ordinal) && p.Text.Contains("The child is done.", StringComparison.Ordinal));
        await WaitForAsync(events, () => LatestParts<TextMessageEventPart>(events, id).Any(p => p.Text == "The helper came back."), cts.Token);
    }

    [OpenCode2Fact]
    public async Task A_prompt_switches_the_agent_and_model_and_the_session_keeps_them()
    {
        const string first = "Review this. (switch 1)";
        const string second = "And this. (switch 2)";
        const string third = "Back to building. (switch 3)";
        fleet.Answer(request => LlmRequest.Starts(request, first) || LlmRequest.Starts(request, second) || LlmRequest.Starts(request, third)
            ? new ScriptedLlmResponse { Text = $"Answered by {LlmRequest.Model(request)}." }
            : null);

        using var cts = new CancellationTokenSource(Timeout);
        var id = await fleet.CreateSessionAsync(fleet.NewFolder("switch"), "Switch", cts.Token);
        var events = fleet.Watch(cts.Token, id);

        // The composer's choice: another agent and another model.
        await PromptAsync(id, first, new PromptOptions { Agent = "reviewer", ProviderId = "fake", ModelId = "fake-model-2" }, cts.Token);
        await WaitForAsync(events, () => events.For(id).Any(e => e.Type == "session.idle"), cts.Token);

        // No choice: the session keeps the last one.
        await PromptAsync(id, second, options: null, cts.Token);
        await WaitForAsync(events, () => events.For(id).Count(e => e.Type == "session.idle") >= 2, cts.Token);

        await PromptAsync(id, third, new PromptOptions { Agent = "build", ProviderId = "fake", ModelId = "fake-model" }, cts.Token);
        await WaitForAsync(events, () => events.For(id).Count(e => e.Type == "session.idle") >= 3, cts.Token);

        var requests = fleet.Llm.Queue.Requests;
        var asked = new[] { first, second, third }.Select(p => requests.Single(r => LlmRequest.Starts(r, p))).ToList();
        asked.Select(LlmRequest.Model).ShouldBe(["fake-model-2", "fake-model-2", "fake-model"]);
        asked.Select(r => LlmRequest.System(r).Contains(OpenCode2LiveFleet.ReviewerMarker, StringComparison.Ordinal)).ShouldBe([true, true, false]);

        // Each reply says who answered it.
        var answers = Messages(events, id).Where(m => m.Info.Role == "assistant")
            .GroupBy(m => m.Info.Id).Select(g => g.Last().Info).ToList();
        answers.Select(a => (a.Agent, a.ModelId)).ShouldBe([("reviewer", "fake-model-2"), ("reviewer", "fake-model-2"), ("build", "fake-model")]);
    }

    [OpenCode2Fact]
    public async Task A_turn_ends_with_a_failure_when_the_server_stops_and_the_next_prompt_starts_a_new_one()
    {
        const string prompt = "Wait for the slow job. (server stops)";
        const string after = "Are you back? (server stops)";
        fleet.Answer(request => LlmRequest.Starts(request, prompt) ? ToolCall("call_slow", "shell", new { command = "sleep 60", description = "Wait for the job" })
            : LlmRequest.Starts(request, after) ? new ScriptedLlmResponse { Text = "Back again." }
            : null);

        using var cts = new CancellationTokenSource(Timeout);
        var id = await fleet.CreateSessionAsync(fleet.NewFolder("server-stops"), "Server stops", cts.Token);
        var events = fleet.Watch(cts.Token, id);

        await PromptAsync(id, prompt, options: null, cts.Token);
        await WaitForAsync(events, () => Parts<ToolMessageEventPart>(events, id).Any(p => p.CallId == "call_slow" && p.State is ToolRunningState), cts.Token);

        var harness = (OpenCode2HarnessSession)await fleet.HarnessSessionAsync(id, cts.Token);
        var stopped = harness.ProcessId.ShouldNotBeNull();
        using (var server = Process.GetProcessById(stopped))
        {
            server.Kill(entireProcessTree: true);
            await server.WaitForExitAsync(cts.Token);
        }

        // The turn doesn't stay "Working": it fails, saying why, and ends.
        await WaitForAsync(events, () => events.For(id).Any(e => e.Type == "session.error"), cts.Token);
        events.For(id).Single(e => e.Type == "session.error").Payload.GetProperty("error").GetProperty("message").GetString()
            .ShouldNotBeNull().ShouldContain("server stopped");
        await WaitForAsync(events, () => events.For(id).Any(e => e.Type == "session.idle"), cts.Token);

        // The same session answers on the owner's next server.
        await PromptAsync(id, after, options: null, cts.Token);
        await WaitForAsync(events, () => events.For(id).Count(e => e.Type == "session.idle") >= 2, cts.Token);
        LatestParts<TextMessageEventPart>(events, id).Last().Text.ShouldBe("Back again.");
        harness.ProcessId.ShouldNotBeNull().ShouldNotBe(stopped);
    }

    [OpenCode2Fact]
    public async Task Stopping_Fleet_stops_its_OpenCode_2_server()
    {
        const string prompt = "Say hi before Fleet stops. (fleet stops)";

        // A Fleet of its own, since this one stops.
        var own = new OpenCode2LiveFleet();
        await own.InitializeAsync();
        int serverId;
        try
        {
            own.Answer(request => LlmRequest.Starts(request, prompt) ? new ScriptedLlmResponse { Text = "Hi." } : null);
            using var cts = new CancellationTokenSource(Timeout);
            var id = await own.CreateSessionAsync(own.NewFolder("fleet-stops"), "Fleet stops", cts.Token);
            var events = own.Watch(cts.Token, id);
            var prompted = await own.WithOrchestratorAsync(o => o.PromptSessionAsync(id, prompt, null, cts.Token));
            prompted.IsSuccess.ShouldBeTrue();
            await WaitForAsync(events, () => events.For(id).Any(e => e.Type == "session.idle"), cts.Token);
            serverId = ((OpenCode2HarnessSession)await own.HarnessSessionAsync(id, cts.Token)).ProcessId.ShouldNotBeNull();
        }
        finally
        {
            await own.DisposeAsync();
        }

        (await HasExitedAsync(serverId, TimeSpan.FromSeconds(10))).ShouldBeTrue($"OpenCode 2 server {serverId} outlived Fleet.");
    }

    [OpenCode2Fact]
    public async Task Stopping_Fleet_stops_the_servers_it_started_later_too()
    {
        // A Fleet of its own, since this one stops.
        var own = new OpenCode2LiveFleet();
        await own.InitializeAsync();
        List<int> servers;
        try
        {
            using var cts = new CancellationTokenSource(Timeout);
            var folder = own.NewFolder("fleet-stops-later");
            var home = own.Runtime.ServerEnvironment["HOME"];
            var work = new HarnessProfile { Id = "later", HarnessType = OpenCode2HarnessSession.Type, Name = "Later", Content = """{ "model": "fake/fake-model" }""" };

            // The owner's server, then its replacement once a setting behind it changed.
            await own.Runtime.GetCatalogAsync(OpenCode2LiveFleet.Owner, folder, profile: null, cts.Token);
            var first = ProcessesWith("HOME", home).ShouldHaveSingleItem();
            using (BackgroundUserContext.BeginScope(OpenCode2LiveFleet.Owner))
            using (var scope = own.Services.CreateScope())
                await scope.ServiceProvider.GetRequiredService<IUserPreferenceRepository>().SetAsync(SessionMessages.PreferenceKey, "true");
            await own.Runtime.GetCatalogAsync(OpenCode2LiveFleet.Owner, folder, profile: null, cts.Token);
            (await HasExitedAsync(first, TimeSpan.FromSeconds(10))).ShouldBeTrue($"Replaced server {first} is still running.");

            // A profile's server, stopped as idle and started again.
            await own.Runtime.GetCatalogAsync(OpenCode2LiveFleet.Owner, folder, work, cts.Token);
            var idled = ProcessesWith("HOME", home);
            idled.Count.ShouldBe(2);
            (await own.Runtime.StopIdleServersAsync(DateTimeOffset.UtcNow.AddHours(1))).ShouldBe(1);
            await own.Runtime.GetCatalogAsync(OpenCode2LiveFleet.Owner, folder, work, cts.Token);

            servers = ProcessesWith("HOME", home);
            servers.Count.ShouldBe(2);
            servers.ShouldNotContain(first);
            servers.Except(idled).ShouldHaveSingleItem();
            servers.ShouldAllBe(pid => ProcessGroupHelper.RunningProcesses.Any(p => p.Pid == pid));
        }
        finally
        {
            await own.DisposeAsync();
        }

        foreach (var server in servers)
            (await HasExitedAsync(server, TimeSpan.FromSeconds(10))).ShouldBeTrue($"OpenCode 2 server {server} outlived Fleet.");
        ProcessGroupHelper.RunningProcesses.ShouldNotContain(p => servers.Contains(p.Pid));
    }

    private static async Task<bool> HasExitedAsync(int processId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                    return true;
            }
            catch (ArgumentException)
            {
                return true;
            }

            await Task.Delay(200);
        }

        return false;
    }

    private async Task PromptAsync(string sessionId, string text, PromptOptions? options, CancellationToken ct)
    {
        var prompted = await fleet.WithOrchestratorAsync(o => o.PromptSessionAsync(sessionId, text, options, ct));
        prompted.IsSuccess.ShouldBeTrue(prompted.IsFailure ? prompted.Error.Description : null);
    }

    private async Task<IReadOnlyList<WeaveFleet.Application.DTOs.DelegationDto>> Delegations(string sessionId)
    {
        using var user = BackgroundUserContext.BeginScope(OpenCode2LiveFleet.Owner);
        using var scope = fleet.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DelegationService>().GetDelegationsAsync(sessionId);
    }

    private static ScriptedLlmResponse ToolCall(string callId, string tool, object input) => new()
    {
        StopReason = "tool_calls",
        ToolCalls = [new ScriptedToolCall(callId, tool, JsonSerializer.Serialize(input))],
    };

    private static object Question(string question) => new
    {
        questions = new[]
        {
            new
            {
                question,
                header = "Pick",
                options = new[] { new { label = "A", description = "The first" }, new { label = "B", description = "The second" } },
            },
        },
    };

    /// <summary>Fleet says a session is working with <c>session.status</c> <c>busy</c>.</summary>
    private static bool IsBusy(BroadcastEvent e)
        => e.Type == "session.status"
            && e.Payload.TryGetProperty("status", out var status)
            && status.ValueKind == JsonValueKind.Object
            && status.TryGetProperty("type", out var type)
            && type.GetString() == ActivityStatuses.Busy;

    private static IEnumerable<T> Parts<T>(LiveEvents events, string sessionId) where T : MessageEventPart
        => events.For(sessionId).Select(e => e.DomainEvent).OfType<MessagePartUpdated>().Select(e => e.Payload.Part).OfType<T>();

    /// <summary>Each part as the live stream last showed it, in the order they first appeared.</summary>
    private static List<T> LatestParts<T>(LiveEvents events, string sessionId) where T : MessageEventPart
        => Parts<T>(events, sessionId).GroupBy(p => p.Id).Select(g => g.Last()).ToList();

    private static IEnumerable<MessageLifecyclePayload> Messages(LiveEvents events, string sessionId)
        => events.For(sessionId).Select(e => e.DomainEvent).OfType<MessageUpdated>().Select(e => e.Payload);

    private static JsonNode ToJson(string sessionId, MessageEventPart part)
        => JsonNode.Parse(JsonSerializer.Serialize(
            new MessagePartUpdatedPayload { SessionId = sessionId, Part = part },
            InfrastructureJsonContext.Default.MessagePartUpdatedPayload))!["part"]!;

    private Task<object> WaitForAsync(LiveEvents events, Func<bool> done, CancellationToken ct)
        => WaitForAsync(events, () => Task.FromResult<object?>(done() ? true : null), ct);

    private Task<object> WaitForAsync(LiveEvents events, Func<Task<bool>> done, CancellationToken ct)
        => WaitForAsync(events, async () => await done() ? true : (object?)null, ct);

    /// <summary>Polls until <paramref name="read"/> has a value; on a timeout, says what Fleet sent and what the model was asked.</summary>
    private async Task<T> WaitForAsync<T>(LiveEvents events, Func<Task<T?>> read, CancellationToken ct)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(TimeSpan.FromSeconds(60));
        while (true)
        {
            if (await read() is { } value)
                return value;

            try
            {
                await Task.Delay(200, wait.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException(
                    $"Timed out. Fleet sent: {events.Describe()}\n" +
                    "The model was asked:\n" + string.Join("\n", fleet.Llm.Queue.Requests.Select(r =>
                        $"  first={Trim(LlmRequest.FirstUserText(r))} last={LlmRequest.LastRole(r)} tool={Trim(LlmRequest.LastToolText(r))}")));
            }
        }
    }

    private static string Trim(string? value) => value is null ? "-" : value[..Math.Min(value.Length, 120)];

    [GeneratedRegex(@"cv_[A-Za-z0-9]+")]
    private static partial Regex CanvasId();
}

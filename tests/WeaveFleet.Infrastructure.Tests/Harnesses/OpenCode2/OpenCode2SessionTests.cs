using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode2;

/// <summary>What an OpenCode 2 session asks its server for: questions, permissions, history, and catching up.</summary>
public sealed class OpenCode2SessionTests
{
    private const string Session = OpenCode2Fixtures.ToolsSession;
    private const string Form = OpenCode2Fixtures.AnsweredQuestionForm;
    private const string Call = OpenCode2Fixtures.AnsweredQuestionCall;

    [Fact]
    public async Task A_new_session_allows_everything_so_nothing_waits_on_a_permission_prompt()
    {
        var api = new StubHandler(_ => Json("""{"data":{"id":"ses_new"}}"""));
        using var client = OpenCode2Fixtures.ClientServing("", api);

        await client.CreateSessionAsync("/work", CancellationToken.None);

        var body = JsonDocument.Parse(api.Requests.ShouldHaveSingleItem().Body!).RootElement;
        body.GetProperty("location").GetProperty("directory").GetString().ShouldBe("/work");
        var rule = body.GetProperty("permissions").EnumerateArray().ShouldHaveSingleItem();
        rule.GetProperty("action").GetString().ShouldBe("*");
        rule.GetProperty("resource").GetString().ShouldBe("*");
        rule.GetProperty("effect").GetString().ShouldBe("allow");
    }

    [Fact]
    public async Task A_permission_ask_that_still_arrives_is_allowed_once()
    {
        var replied = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new StubHandler(request =>
        {
            replied.TrySetResult(request.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        await using var server = Server(api);
        await using var session = NewSession(server);

        session.OnEvent(Event("permission.asked", $$"""
            {"id":"per_1","sessionID":"{{Session}}","action":"shell","resources":["echo from-tool"],"save":["echo *"]}
            """));

        (await replied.Task.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe($"/api/session/{Session}/permission/per_1/reply");
        JsonDocument.Parse(api.Requests.ShouldHaveSingleItem().Body!).RootElement.GetProperty("decision").GetString().ShouldBe("once");
    }

    [Fact]
    public async Task An_answer_from_the_question_card_replies_to_the_form_the_tool_call_asked_with()
    {
        var api = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        await using var server = Server(api);
        await using var session = NewSession(server);
        session.OnEvent(FormCreated());

        // The card knows the tool call and the chosen labels.
        await session.AnswerQuestionAsync(Call, [["B"]], CancellationToken.None);

        var request = api.Requests.ShouldHaveSingleItem();
        request.Path.ShouldBe($"/api/session/{Session}/form/{Form}/reply");
        JsonDocument.Parse(request.Body!).RootElement.GetProperty("answer").GetRawText().ShouldBe("""{"q0":"B"}""");
    }

    [Fact]
    public async Task Answering_starts_the_turn_again()
    {
        await using var server = Server(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent)));
        await using var session = NewSession(server);
        session.OnEvent(FormCreated());

        await session.AnswerQuestionAsync(Call, [["A"]], CancellationToken.None);

        var events = await ReadAvailableAsync(session);
        events.Select(StatusOf).ShouldBe([ActivityStatuses.WaitingInput, ActivityStatuses.Busy]);
    }

    [Fact]
    public async Task A_question_fleet_did_not_see_asked_is_found_on_the_server()
    {
        // After a restart the form was asked before Fleet was listening.
        var api = new StubHandler(request => request.Method == HttpMethod.Get
            ? Json(OpenCode2Fixtures.Read("tools-questions.forms.json"))
            : new HttpResponseMessage(HttpStatusCode.NoContent));
        await using var server = Server(api);
        await using var session = NewSession(server);

        await session.AnswerQuestionAsync(Call, [["A"]], CancellationToken.None);

        api.Requests.Select(r => (r.Method.Method, r.Path)).ShouldBe(
        [
            ("GET", $"/api/session/{Session}/form"),
            ("POST", $"/api/session/{Session}/form/{Form}/reply"),
        ]);
    }

    [Fact]
    public async Task Dismissing_the_question_cancels_its_form()
    {
        var api = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        await using var server = Server(api);
        await using var session = NewSession(server);
        session.OnEvent(FormCreated());

        await session.RejectQuestionAsync(Call, CancellationToken.None);

        var request = api.Requests.ShouldHaveSingleItem();
        (request.Method.Method, request.Path).ShouldBe(("DELETE", $"/api/session/{Session}/form/{Form}"));
    }

    [Fact]
    public async Task An_answer_to_a_question_that_is_gone_says_so()
    {
        await using var server = Server(new StubHandler(_ => Json("""{"data":[]}""")));
        await using var session = NewSession(server);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => session.AnswerQuestionAsync(Call, [["A"]], CancellationToken.None));
        ex.Message.ShouldBe("OpenCode 2 has no open question for this answer any more.");
    }

    [Fact]
    public void Chosen_labels_are_sent_as_values_typed_answers_as_typed_and_a_multiselect_as_a_list()
    {
        var form = JsonSerializer.Deserialize("""
            {"id":"frm_1","sessionID":"ses_1","metadata":{"kind":"question","tool":{"id":"call_1"}},"fields":[
              {"key":"q0","type":"string","options":[{"value":"yes","label":"Yes, do it"},{"value":"no","label":"No"}],"custom":true},
              {"key":"q1","type":"string","options":[{"value":"a","label":"A"}],"custom":true},
              {"key":"q2","type":"multiselect","options":[{"value":"x","label":"X"},{"value":"y","label":"Y"}]},
              {"key":"q3","type":"string","options":[]}]}
            """, OpenCode2JsonContext.Default.OpenCode2Form)!;

        var answer = OpenCode2HarnessSession.FormAnswer(form, [["Yes, do it"], ["something else"], ["X", "Y"], []]);

        answer.Keys.ShouldBe(["q0", "q1", "q2"]);
        answer["q0"].GetString().ShouldBe("yes");
        answer["q1"].GetString().ShouldBe("something else");
        answer["q2"].EnumerateArray().Select(v => v.GetString()).ShouldBe(["x", "y"]);
    }

    [Fact]
    public async Task A_turn_stopped_on_a_question_reads_as_waiting_for_the_user()
    {
        var api = new StubHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/active", StringComparison.Ordinal)
            ? Json(RunningJson)
            : Json(OpenCode2Fixtures.Read("tools-questions.forms.json")));
        await using var server = Server(api);
        await using var session = NewSession(server);

        (await session.GetActivityStatusAsync(CancellationToken.None)).ShouldBe(ActivityStatuses.WaitingInput);
    }

    [Fact]
    public async Task History_comes_from_V2_oldest_first_with_its_cursor_for_older_messages()
    {
        var page1 = JsonDocument.Parse(OpenCode2Fixtures.Read("tools-questions.messages-page1.json")).RootElement;
        var next = page1.GetProperty("cursor").GetProperty("next").GetString()!;
        var api = new StubHandler(request => Json(request.RequestUri!.Query.Contains("cursor=", StringComparison.Ordinal)
            ? OpenCode2Fixtures.Read("tools-questions.messages-page2.json")
            : page1.GetRawText()));
        await using var server = Server(api);
        await using var session = NewSession(server);

        var first = await session.GetMessagesAsync(new MessageQuery(Limit: 3), CancellationToken.None);
        var second = await session.GetMessagesAsync(new MessageQuery(Limit: 3, Before: first.Cursor), CancellationToken.None);

        api.Requests[0].Path.ShouldBe($"/api/session/{Session}/message");
        // Three messages a page: the last page has the user's prompt, then the (dismissed) question.
        first.Messages.Select(m => m.Role).ShouldBe(["user", "assistant"]);
        first.Messages[0].Id.ShouldBe("msg_fleetprompt0004aaaaaaaaaa");
        first.HasMore.ShouldBeTrue();
        first.Cursor.ShouldBe(next);
        second.Messages.Select(m => m.Role).ShouldBe(["user", "assistant", "assistant"]);
        second.Messages[0].Id.ShouldBe("msg_fleetprompt0003aaaaaaaaaa");
    }

    [Fact]
    public async Task A_page_that_is_not_full_is_the_last()
    {
        // V2 sends a next cursor with the last page too.
        await using var server = Server(new StubHandler(_ => Json(OpenCode2Fixtures.Read("tools-questions.messages.json"))));
        await using var session = NewSession(server);

        var page = await session.GetMessagesAsync(new MessageQuery(Limit: 100), CancellationToken.None);

        page.Messages.Count.ShouldBe(11);
        page.HasMore.ShouldBeFalse();
        page.Cursor.ShouldBeNull();
    }

    [Fact]
    public async Task A_turn_that_ended_while_the_stream_was_down_ends_when_it_is_back()
    {
        var api = new StubHandler(_ => Json(OpenCode2Fixtures.Read("tools-questions.messages-page1.json")));
        await using var server = Server(api);
        await using var session = NewSession(server);
        session.OnEvent(Event("session.execution.started", $$"""{"sessionID":"{{Session}}"}"""));

        await session.ResyncAsync(new HashSet<string>(), CancellationToken.None);

        var events = await ReadAvailableAsync(session);
        events[0].Type.ShouldBe(EventTypes.SessionStatus);
        // What was missed shows (the last turn's message and its tool call), then the turn ends.
        events.ShouldContain(e => e.Type == EventTypes.MessagePartUpdated
            && e.Payload!.Value.GetProperty("part").GetProperty("id").GetString() == OpenCode2Mapper.ToolPartId("msg_0b7f9f07e001Afgo9AahHYBdI6", OpenCode2Fixtures.DismissedQuestionCall));
        events[^1].Type.ShouldBe(EventTypes.SessionIdle);
        session.Status.ShouldBe(HarnessSessionStatus.Idle);
    }

    [Fact]
    public async Task A_turn_that_started_while_the_stream_was_down_shows_as_working()
    {
        var api = new StubHandler(request => Json(request.RequestUri!.AbsolutePath.EndsWith("/form", StringComparison.Ordinal)
            ? """{"data":[]}"""
            : """{"data":[],"cursor":{}}"""));
        await using var server = Server(api);
        await using var session = NewSession(server);

        await session.ResyncAsync(new HashSet<string> { Session }, CancellationToken.None);

        (await ReadAvailableAsync(session)).Select(StatusOf).ShouldBe([ActivityStatuses.Busy]);
        session.Status.ShouldBe(HarnessSessionStatus.Running);
    }

    [Fact]
    public async Task An_idle_session_has_nothing_to_catch_up_on()
    {
        var api = new StubHandler(_ => Json("""{"data":[]}"""));
        await using var server = Server(api);
        await using var session = NewSession(server);

        await session.ResyncAsync(new HashSet<string>(), CancellationToken.None);

        api.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task When_the_event_stream_reconnects_the_sessions_catch_up_with_what_is_running()
    {
        // The recorded stream ends; the server opens it again and asks which sessions are running.
        var api = new StubHandler(_ => Json(RunningJson));
        await using var server = new OpenCode2Server(
            "local-user", OpenCode2Fixtures.ClientServing(OpenCode2Fixtures.Read("text-and-tool-turn.sse"), api), "token", process: null, NullLogger.Instance);
        var sink = new ResyncSink();
        server.Attach(Session, sink);

        var active = await sink.Resynced.Task.WaitAsync(TimeSpan.FromSeconds(10));

        active.ShouldBe([Session]);
        api.Requests.ShouldContain(r => r.Path == "/api/session/active");
    }

    private static string RunningJson => "{\"data\":{\"" + Session + "\":{\"type\":\"running\"}}}";

    private static OpenCode2Event FormCreated()
    {
        var form = JsonDocument.Parse(OpenCode2Fixtures.Read("tools-questions.forms.json")).RootElement.GetProperty("data")[0];
        return Event("form.created", "{\"form\":" + form.GetRawText() + "}");
    }

    private static async Task<List<HarnessEvent>> ReadAvailableAsync(OpenCode2HarnessSession session)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var events = new List<HarnessEvent>();
        try
        {
            await foreach (var evt in session.SubscribeAsync(timeout.Token))
                events.Add(evt);
        }
        catch (OperationCanceledException)
        {
            // Read everything written so far.
        }

        return events;
    }

    private static string? StatusOf(HarnessEvent evt)
        => evt.Type == EventTypes.SessionStatus ? evt.Payload!.Value.GetProperty("status").GetProperty("type").GetString() : evt.Type;

    private static OpenCode2Server Server(StubHandler api)
        => new("local-user", OpenCode2Fixtures.ClientServing("", api), "token", process: null, NullLogger.Instance);

    private static OpenCode2HarnessSession NewSession(OpenCode2Server server)
        => new(
            "opencode2-test",
            Session,
            new OpenCode2SessionContext("fleet-session-1", "local-user", "/work", null, null),
            server,
            _ => Task.FromResult(server),
            analytics: null,
            NullLogger.Instance);

    private static OpenCode2Event Event(string type, string data) => new()
    {
        Id = "evt_1",
        Created = 1_789_763_753_000,
        Type = type,
        Data = JsonDocument.Parse(data).RootElement.Clone(),
    };

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class ResyncSink : IOpenCode2EventSink
    {
        public TaskCompletionSource<IReadOnlySet<string>> Resynced { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void OnEvent(OpenCode2Event evt)
        {
        }

        public Task ResyncAsync(IReadOnlySet<string> activeSessions, CancellationToken ct)
        {
            Resynced.TrySetResult(activeSessions);
            return Task.CompletedTask;
        }

        public void OnServerStopped()
        {
        }
    }
}

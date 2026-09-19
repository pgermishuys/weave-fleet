using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode2;

/// <summary>
/// A subagent call and its child session: the call read as a delegation, and the child's events held from its first
/// one until Fleet has a session for it (it starts before Fleet can create one), questions included.
/// </summary>
public sealed class OpenCode2SubagentTests
{
    private const string Parent = OpenCode2Fixtures.SubagentSession;
    private const string Child = "ses_f49bebedbfferDt0V9R7wQ1Y7G";
    private const string Call = "call_1789764124949";

    [Fact]
    public async Task A_recorded_subagent_call_is_read_as_a_delegation_that_finds_its_child_and_completes()
    {
        var mapper = new OpenCode2Mapper("fleet-parent");
        var delegations = new List<OpenCode2Delegation>();
        foreach (var evt in (await OpenCode2Fixtures.ReadEventsAsync("permission-form-subagent-interrupt.sse")).Where(e => e.SessionId == Parent))
        {
            if (mapper.TryReadDelegation(evt) is { } delegation)
                delegations.Add(delegation);
            mapper.Map(evt);
        }

        delegations.ShouldBe(
        [
            new OpenCode2Delegation(Call, "general", "Child work", ChildSessionId: null, "running"),
            new OpenCode2Delegation(Call, "general", "Child work", Child, "running"),
            new OpenCode2Delegation(Call, "general", "Child work", Child, "completed"),
        ]);
    }

    [Fact]
    public void A_failed_subagent_call_is_a_failed_delegation()
    {
        var mapper = new OpenCode2Mapper("fleet-parent");
        mapper.Map(Event("session.tool.input.started", $$"""{"sessionID":"{{Parent}}","assistantMessageID":"msg_1","id":"call_1","name":"subagent"}"""));

        var failed = mapper.TryReadDelegation(Event("session.tool.failed", $$"""
            {"sessionID":"{{Parent}}","assistantMessageID":"msg_1","id":"call_1","error":{"type":"aborted","message":"Interrupted"} }
            """));

        failed.ShouldBe(new OpenCode2Delegation("call_1", "subagent", null, null, "error"));
    }

    [Fact]
    public void Other_tool_calls_are_not_delegations()
    {
        var mapper = new OpenCode2Mapper("fleet-parent");
        mapper.Map(Event("session.tool.input.started", $$"""{"sessionID":"{{Parent}}","assistantMessageID":"msg_1","id":"call_1","name":"shell"}"""));

        mapper.TryReadDelegation(Event("session.tool.progress", $$"""
            {"sessionID":"{{Parent}}","assistantMessageID":"msg_1","id":"call_1","metadata":{"shellID":"sh_1"} }
            """)).ShouldBeNull();
    }

    [Fact]
    public async Task While_the_subagent_runs_its_card_carries_the_child_session()
    {
        var mapper = new OpenCode2Mapper("fleet-parent");
        var progress = new List<HarnessEvent>();
        foreach (var evt in (await OpenCode2Fixtures.ReadEventsAsync("permission-form-subagent-interrupt.sse")).Where(e => e.SessionId == Parent))
        {
            var mapped = mapper.Map(evt);
            if (evt.Type == "session.tool.progress")
                progress.AddRange(mapped);
        }

        var part = progress.ShouldHaveSingleItem().Payload!.Value.GetProperty("part");
        part.GetProperty("tool").GetString().ShouldBe("subagent");
        part.GetProperty("state").GetProperty("status").GetString().ShouldBe("running");
        part.GetProperty("state").GetProperty("input").GetProperty("agent").GetString().ShouldBe("general");
        part.GetProperty("state").GetProperty("metadata").GetProperty("sessionID").GetString().ShouldBe(Child);
    }

    [Fact]
    public async Task A_child_sessions_events_wait_for_fleet_to_attach_it_and_arrive_in_order()
    {
        await using var server = Server(Accepting());
        var parent = new Sink();
        server.Attach(Parent, parent);
        var recorded = (await OpenCode2Fixtures.ReadEventsAsync("permission-form-subagent-interrupt.sse"))
            .Where(e => e.SessionId is Parent or Child)
            .ToList();

        foreach (var evt in recorded)
            server.Route(evt);
        server.IsHolding(Child).ShouldBeTrue();
        var child = new Sink();
        server.Attach(Child, child);

        child.Events.Select(e => e.Id).ShouldBe(recorded.Where(e => e.SessionId == Child).Select(e => e.Id));
        child.Events[0].Type.ShouldBe("session.created");
        child.Events[^1].Type.ShouldBe("session.execution.succeeded");
        parent.Events.ShouldAllBe(e => e.SessionId == Parent);
        server.IsHolding(Child).ShouldBeFalse();
    }

    [Fact]
    public async Task Once_attached_a_child_gets_its_events_straight_away()
    {
        await using var server = Server(Accepting());
        server.Attach(Parent, new Sink());
        server.Route(ChildCreated(Child, Parent));
        var child = new Sink();
        server.Attach(Child, child);

        server.Route(Event("session.execution.started", $$"""{"sessionID":"{{Child}}"}"""));

        child.Events.Select(e => e.Type).ShouldBe(["session.created", "session.execution.started"]);
    }

    [Fact]
    public async Task A_child_of_a_held_child_is_held_too()
    {
        await using var server = Server(Accepting());
        server.Attach(Parent, new Sink());
        server.Route(ChildCreated(Child, Parent));

        server.Route(ChildCreated("ses_grandchild", Child));

        server.IsHolding("ses_grandchild").ShouldBeTrue();
    }

    [Fact]
    public async Task Sessions_fleet_has_nothing_to_do_with_are_not_held()
    {
        await using var server = Server(Accepting());
        server.Attach(Parent, new Sink());

        server.Route(Event("session.created", """{"sessionID":"ses_someone_elses"}"""));
        server.Route(ChildCreated("ses_other_child", "ses_someone_elses"));

        server.IsHolding("ses_someone_elses").ShouldBeFalse();
        server.IsHolding("ses_other_child").ShouldBeFalse();
    }

    [Fact]
    public async Task A_held_childs_permission_ask_is_answered_at_once()
    {
        // The child's turn, and the parent's, wait on it.
        var replied = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = Server(new StubHandler(request =>
        {
            replied.TrySetResult(request.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }));
        server.Attach(Parent, new Sink());
        server.Route(ChildCreated(Child, Parent));

        server.Route(Event("permission.asked", $$"""{"id":"per_1","sessionID":"{{Child}}","action":"shell","resources":["ls"]}"""));

        (await replied.Task.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe($"/api/session/{Child}/permission/per_1/reply");
        var child = new Sink();
        server.Attach(Child, child);
        child.Events.Select(e => e.Type).ShouldBe(["session.created"]);
    }

    [Fact]
    public async Task A_question_the_child_asked_before_fleet_attached_it_shows_and_is_answered_on_the_child()
    {
        var api = Accepting();
        await using var server = Server(api);
        server.Attach(Parent, new Sink());
        server.Route(ChildCreated(Child, Parent));
        server.Route(Event("session.execution.started", $$"""{"sessionID":"{{Child}}"}"""));
        server.Route(ChildQuestion());

        // What EnsureDelegatedChildSessionAsync does through the runtime's ResumeAsync.
        await using var child = new OpenCode2HarnessSession(
            "opencode2-child",
            new OpenCode2SessionInfo { Id = Child, ParentID = Parent },
            new OpenCode2SessionContext("fleet-child", "local-user", "/work", null, null),
            server,
            _ => Task.FromResult(server),
            analytics: null,
            delegations: null,
            NullLogger.Instance);

        (await ReadAvailableAsync(child)).Where(e => e.Type == EventTypes.SessionStatus)
            .Select(e => e.Payload!.Value.GetProperty("status").GetProperty("type").GetString())
            .ShouldBe([ActivityStatuses.Busy, ActivityStatuses.WaitingInput]);
        child.Status.ShouldBe(HarnessSessionStatus.Running);

        await child.AnswerQuestionAsync(OpenCode2Fixtures.AnsweredQuestionCall, [["B"]], CancellationToken.None);

        api.Requests.ShouldContain(r => r.Path == $"/api/session/{Child}/form/{OpenCode2Fixtures.AnsweredQuestionForm}/reply");
    }

    private static OpenCode2Event ChildCreated(string child, string parent)
        => Event("session.created", $$"""{"sessionID":"{{child}}","parentID":"{{parent}}","title":"Child work","agent":"general"}""");

    /// <summary>The question form recorded from a Fleet session, asked by the child instead.</summary>
    private static OpenCode2Event ChildQuestion()
    {
        var form = JsonDocument.Parse(OpenCode2Fixtures.Read("tools-questions.forms.json")).RootElement.GetProperty("data")[0].GetRawText()
            .Replace(OpenCode2Fixtures.ToolsSession, Child, StringComparison.Ordinal);
        return Event("form.created", "{\"form\":" + form + "}");
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

    private static StubHandler Accepting() => new(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

    private static OpenCode2Server Server(StubHandler api)
        => new("local-user", OpenCode2Fixtures.ClientServing("", api), "token", process: null, NullLogger.Instance);

    private static OpenCode2Event Event(string type, string data) => new()
    {
        Id = $"evt_{Guid.NewGuid():N}",
        Created = 1_789_764_124_966,
        Type = type,
        Data = JsonDocument.Parse(data).RootElement.Clone(),
    };

    private sealed class Sink : IOpenCode2EventSink
    {
        public OpenCode2SessionContext Context { get; } = new("fleet-session-1", "local-user", "/work", null, null);

        public List<OpenCode2Event> Events { get; } = [];

        public void OnEvent(OpenCode2Event evt)
        {
            lock (Events)
                Events.Add(evt);
        }

        public Task ResyncAsync(IReadOnlySet<string> activeSessions, CancellationToken ct) => Task.CompletedTask;

        public void OnServerStopped()
        {
        }
    }
}

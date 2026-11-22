using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode2;

/// <summary>
/// A background subagent resumed in the child it already ran in (<c>subagent</c> with <c>sessionID</c>): V2's notices
/// name the child, not the call, so two runs in one child must each end, and a late notice for the first must not
/// end the second.
/// </summary>
public sealed class OpenCode2ResumedSubagentTests
{
    private const string FleetSession = "fleet-session-1";
    private const string Session = "ses_parent";
    private const string Child = "ses_child_x";
    private const string CallA = "call_a";
    private const string CallB = "call_b";

    [Fact]
    public async Task A_resumed_run_after_the_first_ended_by_notice_ends_both_rows()
    {
        await using var server = Server();
        await using var session = NewSession(server);

        Call(session, CallA, resume: false, at: 1_000);
        session.OnEvent(Notice("error", at: 2_000));
        Call(session, CallB, resume: true, at: 3_000);
        session.OnEvent(Notice("completed", at: 4_000));

        (await EndedAsync(session)).ShouldBe([(CallA, WorkEndedReasons.Error), (CallB, WorkEndedReasons.Completed)]);
    }

    [Fact]
    public async Task A_resumed_run_ends_the_first_when_its_notice_was_missed()
    {
        await using var server = Server();
        await using var session = NewSession(server);

        Call(session, CallA, resume: false, at: 1_000);
        Call(session, CallB, resume: true, at: 3_000);
        session.OnEvent(Notice("completed", at: 4_000));

        var ended = await EndedAsync(session);
        ended.Count.ShouldBe(2);
        ended.ShouldContain((CallB, WorkEndedReasons.Completed));
        // The first run was over when the child was resumed; it isn't lost with a server.
        ended.Single(e => e.WorkId == CallA).Reason.ShouldBe(WorkEndedReasons.Cancelled);
    }

    [Fact]
    public async Task A_notice_posted_before_the_resume_is_for_the_earlier_run_and_does_not_end_the_resumed_one()
    {
        await using var server = Server();
        await using var session = NewSession(server);

        Call(session, CallA, resume: false, at: 1_000);
        Call(session, CallB, resume: true, at: 3_000);
        // Reached Fleet late, but V2 posted it before the resume.
        session.OnEvent(Notice("error", at: 2_000));

        var ended = await EndedAsync(session);
        ended.ShouldNotContain(e => e.WorkId == CallB);
        // The resumed run is still the server's to lose.
        session.OnServerStopped(new OpenCode2ServerStop(false));
        (await EndedAsync(session)).ShouldContain((CallB, WorkEndedReasons.Lost));
    }

    [Fact]
    public async Task A_late_error_notice_for_a_run_lost_with_the_server_does_not_end_the_next_run()
    {
        await using var server = Server();
        await using var session = NewSession(server);

        Call(session, CallA, resume: false, at: 1_000);
        session.OnServerStopped(new OpenCode2ServerStop(false));
        Call(session, CallB, resume: true, at: 3_000);
        session.OnEvent(Notice("error", at: 2_000));

        (await EndedAsync(session)).ShouldNotContain(e => e.WorkId == CallB && e.Reason == WorkEndedReasons.Error);
    }

    [Fact]
    public async Task A_notice_posted_after_the_resume_ends_the_resumed_run_and_nothing_is_left_running()
    {
        await using var server = Server();
        await using var session = NewSession(server);

        // The first run failed while the resume was already queued: V2 posts a notice for the child, which is all that
        // can be said about it. Both rows end; neither stays running.
        Call(session, CallA, resume: false, at: 1_000);
        Call(session, CallB, resume: true, at: 3_000);
        session.OnEvent(Notice("error", at: 4_000));
        session.OnEvent(Notice("completed", at: 5_000));
        session.OnServerStopped(new OpenCode2ServerStop(false));

        var ended = await EndedAsync(session);
        ended.Select(e => e.WorkId).Distinct().OrderBy(x => x).ShouldBe([CallA, CallB]);
        ended.ShouldNotContain(e => e.Reason == WorkEndedReasons.Lost);
    }

    private static void Call(OpenCode2HarnessSession session, string call, bool resume, long at)
    {
        var description = resume ? "Research D1 (resume)" : "Research D1";
        var resumeInput = resume ? $$""","sessionID":"{{Child}}" """ : "";
        session.OnEvent(Event("session.tool.input.started", $$"""{"sessionID":"{{Session}}","assistantMessageID":"msg_1","id":"{{call}}","name":"subagent"}""", at));
        session.OnEvent(Event("session.tool.called", $$"""{"sessionID":"{{Session}}","assistantMessageID":"msg_1","id":"{{call}}","input":{"agent":"general","description":"{{description}}","background":true{{resumeInput}}} }""", at));
        session.OnEvent(Event("session.tool.success", $$"""{"sessionID":"{{Session}}","assistantMessageID":"msg_1","id":"{{call}}","content":[],"metadata":{"sessionID":"{{Child}}","status":"running"} }""", at + 5));
    }

    // V2 names the child, not the call, and keeps the child's first description on every later report.
    private static OpenCode2Event Notice(string state, long at) => Event("session.inbox.enqueued", $$"""
        {"inboxID":"msg_n","sessionID":"{{Session}}","item":{"type":"synthetic","payload":{
          "text":"<subagent sessionID=\"{{Child}}\" state=\"{{state}}\" description=\"Research D1\">\nover\n</subagent>",
          "description":"Research D1",
          "metadata":{"source":"subagent","childID":"{{Child}}","agent":"General","state":"{{state}}"} } } }
        """, at);

    private static async Task<List<(string WorkId, string Reason)>> EndedAsync(OpenCode2HarnessSession session)
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
        }

        return events
            .Where(e => e.Type == EventTypes.WorkEnded)
            .Select(e => WorkEvents.Read(e)!)
            .Where(r => r.Kind == WorkKinds.Subagent)
            .Select(r => (r.WorkId, r.EndedReason!))
            .ToList();
    }

    private static OpenCode2Server Server()
        => new("local-user", OpenCode2Fixtures.ClientServing("", new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent))), "token", process: null, NullLogger.Instance);

    private static OpenCode2HarnessSession NewSession(OpenCode2Server server)
        => new(
            "opencode2-test",
            new OpenCode2SessionInfo { Id = Session },
            new OpenCode2SessionContext(FleetSession, "local-user", "/work/proj", null, null),
            server,
            _ => Task.FromResult(server),
            analytics: null,
            NullLogger.Instance);

    private static OpenCode2Event Event(string type, string data, long created) => new()
    {
        Id = "evt_1",
        Created = created,
        Type = type,
        Data = JsonDocument.Parse(data).RootElement.Clone(),
    };
}

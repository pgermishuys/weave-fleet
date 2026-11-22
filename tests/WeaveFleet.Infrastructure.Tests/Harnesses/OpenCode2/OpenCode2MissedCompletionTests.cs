using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode2;

/// <summary>
/// A background subagent that finished and reported back must end as finished in Fleet even when Fleet missed the
/// notice live: the stream was down, the harness session was recreated, or Fleet asks after the fact.
/// </summary>
public sealed class OpenCode2MissedCompletionTests
{
    private const string FleetSession = "fleet-session-1";
    private const string Session = "ses_f35d772d0ffe5Nudf5Dt2Eb6ty";
    private const string Child = "ses_f35d720beffeFpWwyVed1mYSiQ";
    private const string SubagentCall = "call_1790098071345";

    [Fact]
    public async Task A_notice_posted_while_the_stream_was_down_ends_the_delegation_when_it_is_back()
    {
        var api = new StubHandler(_ => Json(OpenCode2Fixtures.Read("background-tasks.messages.json")));
        await using var server = Server(api);
        await using var session = NewSession(server);
        foreach (var evt in await WithoutNoticesAsync())
            session.OnEvent(evt);

        // The parent is idle and no turn runs: only the notice is missing.
        await session.ResyncAsync(new HashSet<string>(), CancellationToken.None);

        var ended = Ended(await ReadAvailableAsync(session)).Where(r => r.Kind == WorkKinds.Subagent).ToList();
        ended.ShouldHaveSingleItem().WorkId.ShouldBe(SubagentCall);
        ended[0].EndedReason.ShouldBe(WorkEndedReasons.Completed);
    }

    [Fact]
    public async Task A_notice_for_a_call_this_session_never_saw_ends_the_work_running_in_that_child()
    {
        await using var server = Server(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent)));
        await using var session = NewSession(server);

        foreach (var evt in (await OpenCode2Fixtures.ReadEventsAsync("background-tasks.sse")).Where(e => IsSubagentNotice(e) && e.SessionId == Session))
            session.OnEvent(evt);

        var ended = Ended(await ReadAvailableAsync(session)).ShouldHaveSingleItem();
        (ended.ChildHarnessSessionId, ended.EndedReason).ShouldBe((Child, WorkEndedReasons.Completed));
    }

    [Fact]
    public async Task A_child_whose_notice_is_in_history_is_reported_finished_not_missing_from_what_runs()
    {
        var api = new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/session/active" => Json("""{"data":{}}"""),
            "/api/shell" => Json("""{"data":[]}"""),
            var path when path.EndsWith("/inbox", StringComparison.Ordinal) => Json("""{"data":[]}"""),
            var path when path.EndsWith("/message", StringComparison.Ordinal) => Json(OpenCode2Fixtures.Read("background-tasks.messages.json")),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        await using var server = Server(api);
        await using var session = NewSession(server);

        var running = (await session.GetRunningWorkAsync(CancellationToken.None)).ShouldNotBeNull();

        var finished = running.Where(r => r.ChildHarnessSessionId == Child).ShouldHaveSingleItem();
        finished.EndedReason.ShouldBe(WorkEndedReasons.Completed);
    }

    private static List<WorkReport> Ended(IEnumerable<HarnessEvent> events)
        => events.Where(e => e.Type == EventTypes.WorkEnded).Select(e => WorkEvents.Read(e)!).ToList();

    private static bool IsSubagentNotice(OpenCode2Event evt)
        => evt.Type == "session.inbox.enqueued"
            && evt.Data.GetProperty("item").GetProperty("type").GetString() == "synthetic"
            && evt.Data.GetProperty("item").GetProperty("payload").GetProperty("metadata").GetProperty("source").GetString() == "subagent";

    private static async Task<IEnumerable<OpenCode2Event>> WithoutNoticesAsync()
        => (await OpenCode2Fixtures.ReadEventsAsync("background-tasks.sse"))
            .Where(e => e.SessionId == Session && !(e.Type == "session.inbox.enqueued" && e.Data.GetProperty("item").GetProperty("type").GetString() == "synthetic"));

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static OpenCode2Server Server(StubHandler api)
        => new("local-user", OpenCode2Fixtures.ClientServing("", api), "token", process: null, NullLogger.Instance);

    private static OpenCode2HarnessSession NewSession(OpenCode2Server server)
        => new(
            "opencode2-test",
            new OpenCode2SessionInfo { Id = Session },
            new OpenCode2SessionContext(FleetSession, "local-user", "/work/proj", null, null),
            server,
            _ => Task.FromResult(server),
            analytics: null,
            NullLogger.Instance);

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
        }

        return events;
    }
}

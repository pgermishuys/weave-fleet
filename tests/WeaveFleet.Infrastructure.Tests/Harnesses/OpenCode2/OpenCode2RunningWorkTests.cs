using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode2;

/// <summary>
/// OpenCode 2's background shells and subagents as Fleet's running work: what the session reports (from the recorded
/// <c>background-tasks.sse</c>), and Stop, Output and the running list against V2's shell routes as a real 2.0.18
/// server answered them (<c>tests/contracts/opencode2-shell-routes.json</c>).
/// </summary>
public sealed class OpenCode2RunningWorkTests
{
    private const string FleetSession = "fleet-session-1";
    private const string Session = "ses_f35d772d0ffe5Nudf5Dt2Eb6ty";
    private const string Child = "ses_f35d720beffeFpWwyVed1mYSiQ";
    private const string Shell = "sh_0ca289192001atCtTyB6Js2f3j";
    private const string ShellCall = "call_1790098051372";
    private const string SubagentCall = "call_1790098071345";

    private static readonly JsonObject Routes = (JsonNode.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "contracts", "opencode2-shell-routes.json")))!["routes"]!).AsObject();

    private static readonly string RecordedShell = Contract("shellId");
    private static readonly string RecordedDirectory = Contract("directory");

    [Fact]
    public async Task A_backgrounded_shell_is_running_work_until_its_notice_ends_it_with_the_exit_code()
    {
        var work = (await MapAsync()).Where(e => EventTypes.IsWorkEvent(e.Type)).Select(e => (e.Type, Report: WorkEvents.Read(e)!))
            .Where(e => e.Report.Kind == WorkKinds.Shell)
            .ToList();

        work.Select(e => e.Type).ShouldBe([EventTypes.WorkStarted, EventTypes.WorkEnded]);
        work[0].Report.ShouldBe(new WorkReport
        {
            WorkId = Shell,
            Kind = WorkKinds.Shell,
            Title = "shell",
            Label = "sleep 6; echo shell-finished-late",
            ToolCallId = ShellCall,
            Background = true,
            CanStop = true,
            CanReadOutput = true,
        });
        work[1].Report.EndedReason.ShouldBe(WorkEndedReasons.Completed);
        work[1].Report.Detail.ShouldBe("exit 0");
        work[1].Report.WorkId.ShouldBe(Shell);
    }

    [Fact]
    public async Task A_subagent_call_is_running_work_that_finds_its_child_goes_to_the_background_and_ends_with_its_notice()
    {
        await using var server = Server(Accepting());
        await using var session = NewSession(server, Session);

        foreach (var evt in await OpenCode2Fixtures.ReadEventsAsync("background-tasks.sse"))
            server.Route(evt);

        var work = (await ReadAvailableAsync(session)).Where(e => EventTypes.IsWorkEvent(e.Type))
            .Select(e => (e.Type, Report: WorkEvents.Read(e)!))
            .Where(e => e.Report.Kind == WorkKinds.Subagent)
            .ToList();

        work.Select(e => e.Type).ShouldBe([EventTypes.WorkStarted, EventTypes.WorkUpdated, EventTypes.WorkUpdated, EventTypes.WorkEnded]);
        work.ShouldAllBe(e => e.Report.WorkId == SubagentCall && e.Report.ToolCallId == SubagentCall);
        work[0].Report.Title.ShouldBe("general");
        work[0].Report.Label.ShouldBe("Slow helper");
        work[0].Report.ChildHarnessSessionId.ShouldBeNull();
        work[0].Report.CanStop.ShouldBeNull();
        work[1].Report.ChildHarnessSessionId.ShouldBe(Child);
        work[1].Report.CanStop.ShouldBe(true);
        work[2].Report.Background.ShouldBe(true);
        work[3].Report.EndedReason.ShouldBe(WorkEndedReasons.Completed);
    }

    [Fact]
    public async Task Work_still_running_when_the_server_stops_is_lost()
    {
        await using var server = Server(Accepting());
        await using var session = NewSession(server, Session);

        // Both calls in the background, and neither notice came.
        foreach (var evt in await WithoutNoticesAsync())
            server.Route(evt);
        session.OnServerStopped();

        var ended = (await ReadAvailableAsync(session)).Where(e => e.Type == EventTypes.WorkEnded).Select(e => WorkEvents.Read(e)!).ToList();
        ended.Select(r => (r.WorkId, r.EndedReason)).ShouldBe(
            [(Shell, WorkEndedReasons.Lost), (SubagentCall, WorkEndedReasons.Lost)], ignoreOrder: true);
        (await session.StopWorkAsync(SubagentCall, CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task Stop_removes_a_background_shell_from_the_server()
    {
        var api = Answering(request => (request.Method.Method, request.RequestUri!.AbsolutePath) switch
        {
            ("GET", var path) when path == $"/api/shell/{RecordedShell}" => Recorded("GET /api/shell/{id}"),
            ("DELETE", var path) when path == $"/api/shell/{RecordedShell}" => Recorded("DELETE /api/shell/{id}"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        await using var server = Server(api);
        await using var session = NewSession(server, Contract("sessionId"));

        (await session.StopWorkAsync(RecordedShell, CancellationToken.None)).ShouldBeTrue();

        api.Requests.Select(r => (r.Method.Method, r.Path)).ShouldBe(
        [
            ("GET", $"/api/shell/{RecordedShell}"),
            ("DELETE", $"/api/shell/{RecordedShell}"),
        ]);
        api.Uris.ShouldAllBe(uri => uri.Contains("location%5Bdirectory%5D=%2Fwork%2Fproj", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stop_of_a_shell_the_server_no_longer_has_is_false_and_deletes_nothing()
    {
        // V2 answers a DELETE of a shell that's gone with 204 as well; only GET says it's gone.
        var api = Answering(request => request.Method == HttpMethod.Get
            ? Recorded("GET /api/shell/{id} (gone)")
            : Recorded("DELETE /api/shell/{id} (gone)"));
        await using var server = Server(api);
        await using var session = NewSession(server, Contract("sessionId"));

        (await session.StopWorkAsync(RecordedShell, CancellationToken.None)).ShouldBeFalse();

        api.Requests.ShouldNotContain(r => r.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task Stop_interrupts_a_subagents_child_session()
    {
        var api = Accepting();
        await using var server = Server(api);
        await using var session = NewSession(server, Session);
        foreach (var evt in await WithoutNoticesAsync())
            server.Route(evt);

        (await session.StopWorkAsync(SubagentCall, CancellationToken.None)).ShouldBeTrue();
        (await session.StopWorkAsync("call_never_made", CancellationToken.None)).ShouldBeFalse();

        api.Posts().Select(p => p.Path).ShouldBe([$"/api/session/{Child}/interrupt"]);
    }

    [Theory]
    [InlineData(0, "first-line\n")]
    [InlineData(6, "line\n")]
    public async Task Output_pages_a_shell_from_the_offset_asked_for(long offset, string expected)
    {
        var api = Answering(request => request.RequestUri!.AbsolutePath == $"/api/shell/{RecordedShell}/output"
            ? Recorded($"GET /api/shell/{{id}}/output?cursor={System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["cursor"]}")
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        await using var server = Server(api);
        await using var session = NewSession(server, Contract("sessionId"));

        var output = await session.ReadWorkOutputAsync(RecordedShell, offset, CancellationToken.None);

        output.ShouldBe(new WorkOutput(expected, NextOffset: 11, Size: 11, Truncated: false));
        api.Uris.ShouldHaveSingleItem().ShouldContain($"cursor={offset}");
    }

    [Fact]
    public async Task Output_of_a_shell_the_server_no_longer_has_is_null()
    {
        await using var server = Server(Answering(_ => Recorded("GET /api/shell/{id}/output (gone)")));
        await using var session = NewSession(server, Contract("sessionId"));

        (await session.ReadWorkOutputAsync(RecordedShell, 0, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task A_subagent_has_no_output_of_its_own()
    {
        await using var server = Server(Accepting());
        await using var session = NewSession(server, Session);
        foreach (var evt in await WithoutNoticesAsync())
            server.Route(evt);

        await Should.ThrowAsync<NotSupportedException>(() => session.ReadWorkOutputAsync(SubagentCall, 0, CancellationToken.None));
    }

    [Fact]
    public async Task The_running_list_is_the_servers_shells_for_this_session_and_its_children_at_work()
    {
        var shells = Routes["GET /api/shell"]!["body"]!.DeepClone();
        // Another session's shell on the same server isn't this session's work.
        shells["data"]!.AsArray().Add(JsonNode.Parse("""{"id":"sh_other","status":"running","command":"x","metadata":{"sessionID":"ses_other"}}"""));
        var api = Answering(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/shell" => Json(shells.ToJsonString()),
            "/api/session/active" => Json("""{"data":{"ses_recorded":{},"ses_child":{},"ses_unrelated":{}}}"""),
            "/api/session/ses_child" => Json("""{"data":{"id":"ses_child","parentID":"ses_recorded"}}"""),
            "/api/session/ses_unrelated" => Json("""{"data":{"id":"ses_unrelated"}}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        await using var server = Server(api);
        await using var session = NewSession(server, Contract("sessionId"));

        var running = (await session.GetRunningWorkAsync(CancellationToken.None)).ShouldNotBeNull();

        running.Select(r => (r.WorkId, r.Kind, r.ChildHarnessSessionId)).ShouldBe(
        [
            (RecordedShell, WorkKinds.Shell, null),
            ("ses_child", WorkKinds.Subagent, "ses_child"),
        ]);
        running[0].Label.ShouldBe("echo first-line; sleep 30; echo never");
        running[0].CanStop.ShouldBe(true);
    }

    [Fact]
    public async Task A_running_list_the_server_wont_give_is_unknown_rather_than_empty()
    {
        await using var server = Server(Answering(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        await using var session = NewSession(server, Contract("sessionId"));

        (await session.GetRunningWorkAsync(CancellationToken.None)).ShouldBeNull();
    }

    private static string Contract(string property)
        => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", "opencode2-shell-routes.json")))![property]!.GetValue<string>();

    /// <summary>The response V2 gave for <paramref name="route"/> in the recording.</summary>
    private static HttpResponseMessage Recorded(string route)
    {
        var recorded = Routes[route] ?? throw new KeyNotFoundException(route);
        var response = new HttpResponseMessage((HttpStatusCode)recorded["status"]!.GetValue<int>());
        if (recorded["body"] is { } body)
            response.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return response;
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static async Task<List<HarnessEvent>> MapAsync()
    {
        var mapper = new OpenCode2Mapper(FleetSession);
        var mapped = new List<HarnessEvent>();
        foreach (var evt in await OpenCode2Fixtures.ReadEventsAsync("background-tasks.sse"))
        {
            if (evt.SessionId == Session)
                mapped.AddRange(mapper.Map(evt));
        }

        return mapped;
    }

    /// <summary>The recording as if the work were still going: both calls in the background, and no notice that either ended.</summary>
    private static async Task<IEnumerable<OpenCode2Event>> WithoutNoticesAsync()
        => (await OpenCode2Fixtures.ReadEventsAsync("background-tasks.sse")).Where(e => !(IsNotice(e) && e.SessionId == Session));

    private static bool IsNotice(OpenCode2Event evt)
        => evt.Type == "session.inbox.enqueued" && evt.Data.GetProperty("item").GetProperty("type").GetString() == "synthetic";

    private static StubHandler Accepting() => new(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

    private static StubHandler Answering(Func<HttpRequestMessage, HttpResponseMessage> respond) => new(respond);

    private static OpenCode2Server Server(StubHandler api)
        => new("local-user", OpenCode2Fixtures.ClientServing("", api), "token", process: null, NullLogger.Instance);

    private static OpenCode2HarnessSession NewSession(OpenCode2Server server, string v2SessionId)
        => new(
            "opencode2-test",
            new OpenCode2SessionInfo { Id = v2SessionId },
            new OpenCode2SessionContext(FleetSession, "local-user", RecordedDirectory, null, null),
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
            // Read everything written so far.
        }

        return events;
    }
}

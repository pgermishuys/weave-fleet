using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode2;

public sealed class OpenCode2RuntimeTests
{
    private const string Session = OpenCode2Fixtures.TextSession;

    [Theory]
    [InlineData("server listening on http://127.0.0.1:35815", "http://127.0.0.1:35815/")]
    [InlineData("server listening on http://127.0.0.1:4802/", "http://127.0.0.1:4802/")]
    public void The_server_address_is_read_from_its_listening_line(string line, string expected)
        => OpenCode2ProcessManager.ParseListeningUrl(line).ShouldBe(new Uri(expected));

    [Theory]
    [InlineData("timestamp=2026-09-18T20:35:51Z level=INFO message=\"database schema bootstrap started\"")]
    [InlineData("")]
    public void Other_output_is_not_a_listening_line(string line)
        => OpenCode2ProcessManager.ParseListeningUrl(line).ShouldBeNull();

    [Theory]
    [InlineData("2.0.6", true)]
    [InlineData("2.0.8", true)]
    [InlineData("2.14.0-beta.1", true)]
    [InlineData("1.18.30", false)]
    [InlineData("3.0.0", false)]
    public void Only_a_2x_version_is_OpenCode_2(string version, bool expected)
        => OpenCode2Executable.IsOpenCode2(version).ShouldBe(expected);

    [Fact]
    public void An_opencode2_that_reports_1x_is_not_working()
    {
        var availability = OpenCode2Executable.RequireOpenCode2(HarnessAvailability.Ready("1.18.30", "/home/you/.opencode/bin/opencode2"));

        availability.State.ShouldBe(HarnessStates.NotWorking);
        availability.Reason.ShouldBe("opencode2 is OpenCode 1.18.30. The OpenCode 2 harness needs OpenCode 2.x.");
        availability.ExecutablePath.ShouldBe("/home/you/.opencode/bin/opencode2");
    }

    [Fact]
    public void A_2x_install_is_ready_and_a_missing_one_stays_not_installed()
    {
        OpenCode2Executable.RequireOpenCode2(HarnessAvailability.Ready("2.0.8", "/x/opencode2")).Available.ShouldBeTrue();
        OpenCode2Executable.RequireOpenCode2(HarnessAvailability.NotInstalled("missing")).State.ShouldBe(HarnessStates.NotInstalled);
    }

    [Fact]
    public async Task The_event_stream_skips_heartbeats_and_unreadable_events()
    {
        const string sse = """
            data: {"id":"evt_1","type":"server.connected","data":{}}

            : heartbeat

            data: {not json

            data: {"id":"evt_2","type":"session.execution.started","data":{"sessionID":"ses_1"}}


            """;
        using var client = OpenCode2Fixtures.ClientServing(sse.Replace("\r\n", "\n"));
        var connected = false;

        var events = new List<OpenCode2Event>();
        await foreach (var evt in client.ReadEventsAsync(() => connected = true, CancellationToken.None))
            events.Add(evt);

        connected.ShouldBeTrue();
        events.Select(e => (e.Id, e.Type, e.SessionId)).ShouldBe(
        [
            ("evt_1", "server.connected", null),
            ("evt_2", "session.execution.started", "ses_1"),
        ]);
    }

    [Fact]
    public async Task Both_recordings_read_in_full()
    {
        (await OpenCode2Fixtures.ReadEventsAsync("text-and-tool-turn.sse")).Count.ShouldBe(60);
        (await OpenCode2Fixtures.ReadEventsAsync("permission-form-subagent-interrupt.sse")).Count.ShouldBe(149);
    }

    [Fact]
    public async Task A_session_is_created_in_its_directory()
    {
        var api = new StubHandler(_ => Json("""{"data":{"id":"ses_new","projectID":"p","location":{"directory":"/work/repo"}}}"""));
        using var client = OpenCode2Fixtures.ClientServing("", api);

        var created = await client.CreateSessionAsync("/work/repo", CancellationToken.None);

        created.Id.ShouldBe("ses_new");
        var request = api.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe(HttpMethod.Post);
        request.Path.ShouldBe("/api/session");
        JsonDocument.Parse(request.Body!).RootElement.GetProperty("location").GetProperty("directory").GetString().ShouldBe("/work/repo");
    }

    [Fact]
    public async Task A_session_the_server_does_not_have_reads_as_null()
    {
        using var client = OpenCode2Fixtures.ClientServing("", new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        (await client.GetSessionAsync("ses_gone", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task A_failed_request_says_what_Fleet_was_doing_and_what_V2_answered()
    {
        using var client = OpenCode2Fixtures.ClientServing("", new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":"bad text"}"""),
        }));

        var ex = await Should.ThrowAsync<HttpRequestException>(() => client.PromptAsync("ses_1", "hi", null, CancellationToken.None));

        ex.Message.ShouldBe("""OpenCode 2 couldn't send the prompt: 400 Bad Request. {"error":"bad text"}""");
    }

    [Fact]
    public async Task The_active_sessions_are_the_keys_of_the_active_map()
    {
        using var client = OpenCode2Fixtures.ClientServing("", new StubHandler(_ => Json("""{"data":{"ses_a":{"type":"running"}}}""")));

        (await client.GetActiveSessionIdsAsync(CancellationToken.None)).ShouldBe(["ses_a"]);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, """{"data":{}}""", true)]
    [InlineData(HttpStatusCode.OK, """{"data":{"ses_a":{"type":"running"}}}""", false)]
    [InlineData(HttpStatusCode.InternalServerError, "", false)]
    public async Task A_server_is_idle_only_when_V2_says_no_session_is_running(HttpStatusCode status, string body, bool idle)
    {
        // A server started with other settings is replaced only when idle, so "can't tell" must not read as idle.
        await using var server = Server(OpenCode2Fixtures.ClientServing("", new StubHandler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        })));

        (await server.IsIdleAsync(CancellationToken.None)).ShouldBe(idle);
    }

    [Fact]
    public async Task The_server_routes_each_event_to_the_session_it_is_about()
    {
        var sink = new RecordingSink(stopAt: "session.execution.succeeded");
        var other = new RecordingSink();
        await using var server = Server(OpenCode2Fixtures.ClientServing(OpenCode2Fixtures.Read("text-and-tool-turn.sse")));
        server.Attach(Session, sink);
        server.Attach("ses_someone_else", other);

        await server.WaitForEventsAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        await sink.Stopped.WaitAsync(TimeSpan.FromSeconds(5));

        sink.Events.ShouldAllBe(e => e.SessionId == Session);
        sink.Events[0].Type.ShouldBe("session.created");
        other.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_prompt_goes_to_the_V2_session_under_the_id_Fleet_showed_it_with()
    {
        var api = new StubHandler(_ => Json("""{"data":{"id":"msg_1"}}"""));
        await using var server = Server(OpenCode2Fixtures.ClientServing("", api));
        await using var session = NewSession(server, _ => Task.FromResult(server));

        await session.SendPromptAsync("say hello", new PromptOptions { MessageId = "msg_fleet_1" }, CancellationToken.None);

        var request = api.Requests.ShouldHaveSingleItem();
        request.Path.ShouldBe($"/api/session/{Session}/prompt");
        var body = JsonDocument.Parse(request.Body!).RootElement;
        body.GetProperty("id").GetString().ShouldBe("msg_fleet_1");
        body.GetProperty("text").GetString().ShouldBe("say hello");
    }

    [Fact]
    public async Task A_session_streams_its_mapped_events_and_follows_the_turn()
    {
        await using var server = Server(OpenCode2Fixtures.ClientServing(OpenCode2Fixtures.Read("text-and-tool-turn.sse")));
        await using var session = NewSession(server, _ => Task.FromResult(server));

        // The recording holds two turns. Stopping at the first idle races the pump into the second turn's start.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = new List<HarnessEvent>();
        await foreach (var evt in session.SubscribeAsync(timeout.Token))
        {
            received.Add(evt);
            if (received.Count(e => e.Type == EventTypes.SessionIdle) == 2)
                break;
        }

        received[0].Type.ShouldBe(EventTypes.SessionStatus);
        received.ShouldContain(e => e.Type == EventTypes.MessagePartDelta);
        session.Status.ShouldBe(HarnessSessionStatus.Idle);
    }

    [Fact]
    public async Task A_server_that_stops_mid_turn_ends_the_turn_with_a_failure()
    {
        var server = Server(OpenCode2Fixtures.ClientServing(""));
        await using var session = NewSession(server, _ => Task.FromResult(server));
        session.OnEvent(new OpenCode2Event { Type = "session.execution.started", Data = JsonDocument.Parse($$"""{"sessionID":"{{Session}}"}""").RootElement });

        await server.DisposeAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = new List<HarnessEvent>();
        await foreach (var evt in session.SubscribeAsync(timeout.Token))
        {
            received.Add(evt);
            if (evt.Type == EventTypes.SessionIdle)
                break;
        }

        received.Select(e => e.Type).ShouldBe([EventTypes.SessionStatus, EventTypes.SessionError, EventTypes.SessionIdle]);
        session.Status.ShouldBe(HarnessSessionStatus.Idle);
        (await session.CheckHealthAsync(CancellationToken.None)).Healthy.ShouldBeFalse();
    }

    [Fact]
    public async Task After_its_server_stops_the_next_prompt_goes_to_the_owners_new_server()
    {
        var oldServer = Server(OpenCode2Fixtures.ClientServing(""));
        var api = new StubHandler(request => request.Method == HttpMethod.Get
            ? Json($$$$"""{"data":{"id":"{{{{Session}}}}","projectID":"p","location":{"directory":"/work"}}}""")
            : Json("""{"data":{"id":"msg_1"}}"""));
        await using var newServer = Server(OpenCode2Fixtures.ClientServing("", api));
        await using var session = NewSession(oldServer, _ => Task.FromResult(newServer));

        await oldServer.DisposeAsync();
        await session.SendPromptAsync("again", null, CancellationToken.None);

        api.Requests.Select(r => (r.Method.Method, r.Path)).ShouldBe(
        [
            ("GET", $"/api/session/{Session}"),
            ("POST", $"/api/session/{Session}/prompt"),
        ]);
        (await session.CheckHealthAsync(CancellationToken.None)).Healthy.ShouldBeTrue();
    }

    [Fact]
    public async Task A_session_the_new_server_does_not_know_fails_the_prompt()
    {
        var oldServer = Server(OpenCode2Fixtures.ClientServing(""));
        await using var newServer = Server(OpenCode2Fixtures.ClientServing("", new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))));
        await using var session = NewSession(oldServer, _ => Task.FromResult(newServer));

        await oldServer.DisposeAsync();

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => session.SendPromptAsync("again", null, CancellationToken.None));
        ex.Message.ShouldBe($"OpenCode 2 has no session {Session} any more.");
    }

    [Fact]
    public async Task Attachments_are_refused_rather_than_dropped()
    {
        await using var server = Server(OpenCode2Fixtures.ClientServing(""));
        await using var session = NewSession(server, _ => Task.FromResult(server));

        await Should.ThrowAsync<NotSupportedException>(() => session.SendPromptAsync("look", new PromptOptions
        {
            Attachments = [new HarnessAttachment("image/png", "a.png", "AAAA")],
        }, CancellationToken.None));
    }

    [Fact]
    public async Task The_resume_token_is_the_V2_session_id()
    {
        await using var server = Server(OpenCode2Fixtures.ClientServing(""));
        await using var session = NewSession(server, _ => Task.FromResult(server));

        ((IHarnessSession)session).ResumeToken.ShouldBe(Session);
        session.HarnessType.ShouldBe("opencode2");
    }

    private static OpenCode2Server Server(OpenCode2HttpClient client)
        => new("local-user", client, "token", process: null, NullLogger.Instance);

    private static OpenCode2HarnessSession NewSession(OpenCode2Server server, Func<CancellationToken, Task<OpenCode2Server>> servers)
        => new(
            "opencode2-test",
            Session,
            new OpenCode2SessionContext("fleet-session-1", "local-user", "/work", null, null),
            server,
            servers,
            analytics: null,
            NullLogger.Instance);

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class RecordingSink(string? stopAt = null) : IOpenCode2EventSink
    {
        private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<OpenCode2Event> Events { get; } = [];

        public OpenCode2SessionContext Context { get; } = new("fleet-session-1", "local-user", "/work", null, null);

        public Task Stopped => _stopped.Task;

        public void OnEvent(OpenCode2Event evt)
        {
            if (_stopped.Task.IsCompleted)
                return;
            Events.Add(evt);
            if (evt.Type == stopAt)
                _stopped.TrySetResult();
        }

        public Task ResyncAsync(IReadOnlySet<string> activeSessions, CancellationToken ct) => Task.CompletedTask;

        public void OnServerStopped() => _stopped.TrySetResult();
    }
}

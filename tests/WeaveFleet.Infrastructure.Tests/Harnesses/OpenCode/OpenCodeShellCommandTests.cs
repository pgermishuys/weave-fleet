using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;
using WeaveFleet.Infrastructure.Harnesses.OpenCode.Pooling;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode;

/// <summary>
/// Shell commands the user runs from the composer on OpenCode (1.x): <c>POST /session/{id}/shell</c>, which OpenCode
/// records as a user message (a synthetic note) and an assistant message holding the command as a <c>bash</c> part.
/// Fleet shows the pair as one message of the user's (<see cref="ShellCommands.Role"/>), live and from history.
/// </summary>
public sealed class OpenCodeShellCommandTests
{
    private const string Directory = "/repo/one";
    private const string EchoNote = "msg_0d206f47b001XyDFtHJ87g4wBt";
    private const string EchoCommand = "msg_0d206f7d4001NPFo9w00fBf33D";
    private const string FailingCommand = "msg_0d206f84c001c9B8AKWoYHNrmE";

    /// <summary>
    /// A session's history as OpenCode 1.18.31 returned it (paths shortened): <c>echo hello</c> and
    /// <c>ls /nope; exit 3</c> run from the composer, then a prompt and its reply.
    /// </summary>
    private const string RecordedHistory = """
        [
            {"info":{"id":"msg_0d206f47b001XyDFtHJ87g4wBt","sessionID":"oc-1","role":"user","time":{"created":1790230066299},"agent":"build","model":{"providerID":"fakellm","modelID":"fake-model"}},"parts":[{"id":"prt_0d206f7cc001UC92JKcF9c7T6y","messageID":"msg_0d206f47b001XyDFtHJ87g4wBt","type":"text","text":"The following tool was executed by the user","synthetic":true}]},
            {"info":{"id":"msg_0d206f7d4001NPFo9w00fBf33D","sessionID":"oc-1","role":"assistant","time":{"created":1790230067156,"completed":1790230067235},"parentID":"msg_0d206f47b001XyDFtHJ87g4wBt","modelID":"fake-model","providerID":"fakellm","mode":"build","agent":"build","cost":0,"tokens":{"input":0,"output":0,"reasoning":0,"cache":{"read":0,"write":0}}},"parts":[{"id":"prt_0d206f7d7001cKXSSElU77Q5i3","messageID":"msg_0d206f7d4001NPFo9w00fBf33D","type":"tool","callID":"01M390DXYRD8GTWFGVYTE0XAH4","tool":"bash","state":{"status":"completed","input":{"command":"echo hello"},"output":"hello\n","title":"","metadata":{"output":"hello\n"},"time":{"start":1790230067159,"end":1790230067235}}}]},
            {"info":{"id":"msg_0d206f847001Gy0e8hcuY5PNUS","sessionID":"oc-1","role":"user","time":{"created":1790230067271},"agent":"build","model":{"providerID":"fakellm","modelID":"fake-model"}},"parts":[{"id":"prt_0d206f8490011Y9Y0Pckw0f2Sp","messageID":"msg_0d206f847001Gy0e8hcuY5PNUS","type":"text","text":"The following tool was executed by the user","synthetic":true}]},
            {"info":{"id":"msg_0d206f84c001c9B8AKWoYHNrmE","sessionID":"oc-1","role":"assistant","time":{"created":1790230067276,"completed":1790230067317},"parentID":"msg_0d206f847001Gy0e8hcuY5PNUS","modelID":"fake-model","providerID":"fakellm","mode":"build","agent":"build","cost":0,"tokens":{"input":0,"output":0,"reasoning":0,"cache":{"read":0,"write":0}}},"parts":[{"id":"prt_0d206f84e001hFNgY6cxASh8K9","messageID":"msg_0d206f84c001c9B8AKWoYHNrmE","type":"tool","callID":"01M390DY2EZ28QYVH6VAT0211M","tool":"bash","state":{"status":"completed","input":{"command":"ls /nope; exit 3"},"output":"ls: cannot access '/nope': No such file or directory\n","title":"","metadata":{"output":"ls: cannot access '/nope': No such file or directory\n"},"time":{"start":1790230067278,"end":1790230067317}}}]},
            {"info":{"id":"msg_0d206f8900012FyCkESI2x7WMt","sessionID":"oc-1","role":"user","time":{"created":1790230067344},"agent":"build","model":{"providerID":"fakellm","modelID":"fake-model"}},"parts":[{"id":"prt_0d206f899001A9eFpHT9LjoAES","messageID":"msg_0d206f8900012FyCkESI2x7WMt","type":"text","text":"slow please"}]},
            {"info":{"id":"msg_0d206f8ab0015cUvQkcj4tjcik","sessionID":"oc-1","role":"assistant","time":{"created":1790230067371,"completed":1790230085195},"parentID":"msg_0d206f8900012FyCkESI2x7WMt","modelID":"fake-model","providerID":"fakellm","mode":"build","agent":"build","cost":0,"tokens":{"input":10,"output":5,"reasoning":0,"cache":{"read":0,"write":0}},"finish":"stop"},"parts":[{"id":"prt_0d206fd9f0011NIIBDaSZQUn05","messageID":"msg_0d206f8ab0015cUvQkcj4tjcik","type":"step-start","snapshot":"2e81171448eb9f2ee3821e3d447aa6b2fe3ddba1"},{"id":"prt_0d206fda300168gGqAXtJR9c0r","messageID":"msg_0d206f8ab0015cUvQkcj4tjcik","type":"text","text":"This is a slow reply that takes a while to finish."},{"id":"prt_0d2073e20001gn2UzWSnqdQxpR","messageID":"msg_0d206f8ab0015cUvQkcj4tjcik","type":"step-finish","reason":"stop","snapshot":"2e81171448eb9f2ee3821e3d447aa6b2fe3ddba1","cost":0,"tokens":{"input":10,"output":5,"reasoning":0,"cache":{"read":0,"write":0}}}]}
        ]
        """;

    [Fact]
    public async Task History_shows_each_command_as_the_users_and_leaves_out_OpenCodes_note()
    {
        var http = new ScriptedHandler().On("GET /session/oc-1/message", RecordedHistory);
        await using var session = CreateSession(http);

        var page = await session.GetMessagesAsync(null, CancellationToken.None);

        page.Messages.Select(m => (m.Id, m.Role)).ShouldBe([
            (EchoCommand, ShellCommands.Role),
            (FailingCommand, ShellCommands.Role),
            ("msg_0d206f8900012FyCkESI2x7WMt", "user"),
            ("msg_0d206f8ab0015cUvQkcj4tjcik", "assistant"),
        ]);
        var failing = page.Messages[1].Parts.OfType<ToolUsePart>().ShouldHaveSingleItem();
        failing.Arguments.GetProperty("command").GetString().ShouldBe("ls /nope; exit 3");
        failing.Output!.Value.GetString().ShouldBe("ls: cannot access '/nope': No such file or directory\n");
    }

    [Fact]
    public async Task A_command_whose_note_is_on_an_older_page_is_still_the_users()
    {
        var newest = JsonSerializer.Serialize(JsonDocument.Parse(RecordedHistory).RootElement.EnumerateArray().Skip(3).ToList());
        var http = new ScriptedHandler().On("GET /session/oc-1/message", newest);
        await using var session = CreateSession(http);

        var page = await session.GetMessagesAsync(null, CancellationToken.None);

        page.Messages.Select(m => m.Role).ShouldBe([ShellCommands.Role, "user", "assistant"]);
    }

    [Fact]
    public async Task A_reply_of_the_agents_is_never_taken_for_a_command()
    {
        // A turn's reply that only ran bash still starts with a step.
        var http = new ScriptedHandler().On("GET /session/oc-1/message", """
            [{"info":{"id":"msg_2","sessionID":"oc-1","role":"assistant","parentID":"msg_older","time":{"created":2}},
              "parts":[{"id":"prt_1","messageID":"msg_2","type":"step-start"},{"id":"prt_2","messageID":"msg_2","type":"tool","callID":"c1","tool":"bash","state":{"status":"completed","input":{"command":"ls"},"output":"a"}}]}]
            """);
        await using var session = CreateSession(http);

        var page = await session.GetMessagesAsync(null, CancellationToken.None);

        page.Messages.ShouldHaveSingleItem().Role.ShouldBe("assistant");
    }

    [Fact]
    public void Live_the_note_and_the_reply_to_it_are_recognised()
    {
        var notePart = JsonDocument.Parse($$$"""
            {"sessionID":"oc-1","part":{"id":"prt_1","messageID":"{{{EchoNote}}}","type":"text","text":"The following tool was executed by the user","synthetic":true}}
            """).RootElement;
        var typedPart = JsonDocument.Parse("""
            {"sessionID":"oc-1","part":{"id":"prt_2","messageID":"msg_9","type":"text","text":"The following tool was executed by the user"}}
            """).RootElement;
        var reply = JsonDocument.Parse($$$"""
            {"info":{"id":"{{{EchoCommand}}}","role":"assistant","parentID":"{{{EchoNote}}}","time":{"created":1} } }
            """).RootElement;

        OpenCodeMapper.TryReadShellCommandMarker(notePart).ShouldBe(EchoNote);
        // The same words typed by the user aren't OpenCode's note.
        OpenCodeMapper.TryReadShellCommandMarker(typedPart).ShouldBeNull();
        OpenCodeMapper.TryReadAssistantParentId(reply).ShouldBe(EchoNote);

        var relabelled = OpenCodeMapper.AsShellCommand(new HarnessEvent { Type = EventTypes.MessageUpdated, SessionId = "oc-1", Timestamp = DateTimeOffset.UtcNow, Payload = reply });
        relabelled.Payload!.Value.GetProperty("info").GetProperty("role").GetString().ShouldBe(ShellCommands.Role);
        relabelled.Payload!.Value.GetProperty("info").GetProperty("id").GetString().ShouldBe(EchoCommand);
    }

    [Fact]
    public async Task Running_a_command_files_it_under_the_sessions_agent_and_the_id_fleet_gave_it()
    {
        var http = new ScriptedHandler().On("POST /session/oc-1/shell", """{"info":{"id":"msg_x","role":"assistant"},"parts":[]}""");
        await using var session = CreateSession(http);

        await session.RunShellCommandAsync(
            new ShellCommandOptions { Command = "git status", MessageId = "msg_fleet0001", Agent = "reviewer", ProviderId = "fakellm", ModelId = "fake-model" },
            CancellationToken.None);

        using var body = JsonDocument.Parse(http.Body("POST /session/oc-1/shell"));
        body.RootElement.GetProperty("command").GetString().ShouldBe("git status");
        body.RootElement.GetProperty("agent").GetString().ShouldBe("reviewer");
        body.RootElement.GetProperty("messageID").GetString().ShouldBe("msg_fleet0001");
        body.RootElement.GetProperty("model").GetProperty("providerID").GetString().ShouldBe("fakellm");
        body.RootElement.GetProperty("model").GetProperty("modelID").GetString().ShouldBe("fake-model");
    }

    [Fact]
    public async Task A_session_without_an_agent_uses_the_one_opencode_would()
    {
        var http = new ScriptedHandler()
            .On("GET /agent", """[{"name":"build","mode":"primary"},{"name":"plan","mode":"primary"}]""")
            .On("GET /config", """{"default_agent":"plan"}""")
            .On("POST /session/oc-1/shell", """{"info":{"id":"msg_x","role":"assistant"},"parts":[]}""");
        await using var session = CreateSession(http);

        await session.RunShellCommandAsync(new ShellCommandOptions { Command = "ls" }, CancellationToken.None);

        using var body = JsonDocument.Parse(http.Body("POST /session/oc-1/shell"));
        body.RootElement.GetProperty("agent").GetString().ShouldBe("plan");
        body.RootElement.TryGetProperty("model", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task A_command_during_a_turn_is_refused_saying_why()
    {
        var http = new ScriptedHandler().On(
            "POST /session/oc-1/shell",
            """{"_tag":"SessionBusyError","sessionID":"oc-1","message":"Session is busy: oc-1"}""",
            HttpStatusCode.Conflict);
        await using var session = CreateSession(http);

        var refused = await Should.ThrowAsync<HarnessBusyException>(() => session.RunShellCommandAsync(
            new ShellCommandOptions { Command = "ls", Agent = "build" }, CancellationToken.None));

        refused.Message.ShouldBe("The agent is working. Run the command when its turn ends.");
    }

    [Fact]
    public async Task A_long_command_returns_once_opencode_has_had_time_to_refuse_it()
    {
        // OpenCode answers only when the command ends; Fleet doesn't wait for that.
        var http = new ScriptedHandler().Hang("POST /session/oc-1/shell");
        await using var session = CreateSession(http);

        var run = session.RunShellCommandAsync(new ShellCommandOptions { Command = "sleep 600", Agent = "build" }, CancellationToken.None);

        await run.WaitAsync(TimeSpan.FromSeconds(10));
        http.Requests.Select(r => r.Key).ShouldBe(["POST /session/oc-1/shell"]);
    }

    private static OpenCodeHarnessSession CreateSession(ScriptedHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234") };
        return new OpenCodeHarnessSession(
            instanceId: "test-instance",
            fleetSessionId: "fleet-session-1",
            instanceHandle: new FakeInstanceHandle(new OpenCodeHttpClient(httpClient, NullLogger<OpenCodeHttpClient>.Instance)),
            workingDirectory: Directory,
            scopeFactory: new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            logger: NullLogger<OpenCodeHarnessSession>.Instance,
            ownerUserId: "user-1",
            openCodeSessionId: "oc-1");
    }

    /// <summary>Answers requests by "METHOD /path", recording each one in order.</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (string Body, HttpStatusCode Status)> _responses = new(StringComparer.Ordinal);
        private readonly HashSet<string> _hanging = new(StringComparer.Ordinal);
        private readonly Lock _gate = new();

        public List<KeyValuePair<string, string>> Requests { get; } = [];

        public ScriptedHandler On(string key, string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _responses[key] = (body, status);
            return this;
        }

        public ScriptedHandler Hang(string key)
        {
            _hanging.Add(key);
            return this;
        }

        public string Body(string key)
        {
            lock (_gate)
            {
                return Requests.First(r => r.Key == key).Value;
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            System.Web.HttpUtility.ParseQueryString(uri.Query)["directory"].ShouldBe(Directory);
            var key = $"{request.Method} {uri.AbsolutePath}";
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (_gate)
            {
                Requests.Add(new(key, body));
            }

            if (_hanging.Contains(key))
                await Task.Delay(Timeout.Infinite, cancellationToken);

            if (!_responses.TryGetValue(key, out var response))
                throw new InvalidOperationException($"Unscripted request: {key}");

            return new HttpResponseMessage(response.Status)
            {
                Content = new StringContent(response.Body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class FakeInstanceHandle(OpenCodeHttpClient httpClient) : IOpenCodeInstanceHandle
    {
        public event EventHandler<int>? ProcessExited { add { } remove { } }

        public OpenCodeHttpClient HttpClient { get; } = httpClient;

        public int? ProcessId => null;

        public bool IsRunning => true;

        public Task EnsureConnectedAsync(CancellationToken ct) => Task.CompletedTask;

        public Task WaitForEventSubscriptionAsync(string openCodeSessionId, CancellationToken ct) => Task.CompletedTask;

        public Task SendCommandAsync(string openCodeSessionId, OpenCodeCommandRequest request, CancellationToken ct) => Task.CompletedTask;

        public Task RunShellAsync(string openCodeSessionId, OpenCodeShellRequest request, CancellationToken ct) =>
            HttpClient.RunShellAsync(openCodeSessionId, request, Directory, ct);

        public IAsyncEnumerable<OpenCodeSseEvent> SubscribeEvents(string? openCodeSessionId, CancellationToken ct) => AsyncEnumerable.Empty<OpenCodeSseEvent>();

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

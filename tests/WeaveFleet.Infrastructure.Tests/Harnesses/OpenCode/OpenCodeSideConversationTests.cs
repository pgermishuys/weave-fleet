using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;
using WeaveFleet.Infrastructure.Harnesses.OpenCode.Pooling;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode;

/// <summary>
/// Side conversations (<c>/btw</c>) on OpenCode (1.x): the session is forked (<c>POST /session/{id}/fork</c> with
/// <c>messageID</c>, which copies the messages before it) at its last finished turn, never mid-turn, and Fleet's notes
/// to the model go as synthetic text parts that the conversation doesn't show.
/// </summary>
public sealed class OpenCodeSideConversationTests
{
    private const string Directory = "/repo/one";

    private static string User(string id, string text) =>
        "{\"info\":{\"id\":\"" + id + "\",\"sessionID\":\"oc-1\",\"role\":\"user\",\"time\":{\"created\":1}},"
        + "\"parts\":[{\"id\":\"prt_" + id + "\",\"messageID\":\"" + id + "\",\"type\":\"text\",\"text\":\"" + text + "\"}]}";

    private static string Reply(string id, string? finish, bool completed = true) =>
        "{\"info\":{\"id\":\"" + id + "\",\"sessionID\":\"oc-1\",\"role\":\"assistant\",\"time\":{\"created\":2"
        + (completed ? ",\"completed\":3" : "") + "}" + (finish is null ? "" : ",\"finish\":\"" + finish + "\"") + "},"
        + "\"parts\":[{\"id\":\"prt_" + id + "\",\"messageID\":\"" + id + "\",\"type\":\"text\",\"text\":\"reply\"}]}";

    private static string Page(params string[] messages) => "[" + string.Join(",", messages) + "]";

    private static ScriptedHandler Forking(string history) => new ScriptedHandler()
        .On("GET /session/oc-1/message", history)
        .On("POST /session/oc-1/fork", """{"id":"oc-fork","title":"x (fork #1)"}""")
        .On("GET /session/oc-fork/message", Page(User("msg_copy1", "first"), Reply("msg_copy2", "stop")));

    [Fact]
    public async Task Forks_before_a_turn_that_is_still_running()
    {
        var http = Forking(Page(User("msg_1", "first"), Reply("msg_2", "stop"), User("msg_3", "slow please"), Reply("msg_4", null, completed: false)));
        await using var session = CreateSession(http);

        var fork = await session.ForkSideConversationAsync(CancellationToken.None);

        fork.ShouldBe(new SideConversationFork("oc-fork", "msg_copy2"));
        using var body = JsonDocument.Parse(http.Body("POST /session/oc-1/fork"));
        body.RootElement.GetProperty("messageID").GetString().ShouldBe("msg_3");
    }

    [Fact]
    public async Task A_step_that_stopped_for_tool_calls_doesnt_end_the_turn()
    {
        var http = Forking(Page(
            User("msg_1", "first"), Reply("msg_2", "stop"),
            User("msg_3", "use a tool"), Reply("msg_4", "tool-calls"), Reply("msg_5", null, completed: false)));
        await using var session = CreateSession(http);

        await session.ForkSideConversationAsync(CancellationToken.None);

        using var body = JsonDocument.Parse(http.Body("POST /session/oc-1/fork"));
        body.RootElement.GetProperty("messageID").GetString().ShouldBe("msg_3");
    }

    [Fact]
    public async Task Copies_everything_when_the_last_turn_is_finished()
    {
        var http = Forking(Page(User("msg_1", "first"), Reply("msg_2", "stop")));
        await using var session = CreateSession(http);

        await session.ForkSideConversationAsync(CancellationToken.None);

        using var body = JsonDocument.Parse(http.Body("POST /session/oc-1/fork"));
        body.RootElement.TryGetProperty("messageID", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Copies_nothing_while_the_first_turn_runs()
    {
        var http = Forking(Page(User("msg_1", "first"), Reply("msg_2", null, completed: false)));
        await using var session = CreateSession(http);

        await session.ForkSideConversationAsync(CancellationToken.None);

        using var body = JsonDocument.Parse(http.Body("POST /session/oc-1/fork"));
        body.RootElement.GetProperty("messageID").GetString().ShouldBe("msg_1");
    }

    [Fact]
    public void An_errored_or_aborted_reply_ends_its_turn()
    {
        var aborted = JsonSerializer.Deserialize(
            """{"role":"assistant","id":"m","time":{"created":1,"completed":2},"finish":"tool-calls","error":{"name":"MessageAbortedError"}}""",
            OpenCodeJsonContext.Default.OpenCodeMessageInfo)!;
        var running = JsonSerializer.Deserialize(
            """{"role":"assistant","id":"m","time":{"created":1}}""",
            OpenCodeJsonContext.Default.OpenCodeMessageInfo)!;

        OpenCodeHarnessSession.EndsTurn(aborted).ShouldBeTrue();
        OpenCodeHarnessSession.EndsTurn(running).ShouldBeFalse();
    }

    [Fact]
    public async Task Notes_to_the_model_go_as_synthetic_parts_before_the_prompt()
    {
        var http = new ScriptedHandler().On("POST /session/oc-1/prompt_async", "", HttpStatusCode.NoContent);
        await using var session = CreateSession(http);

        await session.SendPromptAsync("what did I ask first?", new PromptOptions { ModelNotes = ["reference only"] }, CancellationToken.None);

        using var body = JsonDocument.Parse(http.Body("POST /session/oc-1/prompt_async"));
        var parts = body.RootElement.GetProperty("parts").EnumerateArray().ToList();
        parts.Count.ShouldBe(2);
        parts[0].GetProperty("text").GetString().ShouldBe("reference only");
        parts[0].GetProperty("synthetic").GetBoolean().ShouldBeTrue();
        parts[1].GetProperty("text").GetString().ShouldBe("what did I ask first?");
        parts[1].TryGetProperty("synthetic", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task History_leaves_out_synthetic_text_in_the_users_messages()
    {
        var http = new ScriptedHandler().On("GET /session/oc-1/message", """
            [{"info":{"id":"msg_1","sessionID":"oc-1","role":"user","time":{"created":1}},"parts":[
              {"id":"prt_1","messageID":"msg_1","type":"text","text":"reference only","synthetic":true},
              {"id":"prt_2","messageID":"msg_1","type":"text","text":"what did I ask first?"}]}]
            """);
        await using var session = CreateSession(http);

        var page = await session.GetMessagesAsync(null, CancellationToken.None);

        page.Messages.ShouldHaveSingleItem().Parts.OfType<TextPart>().Select(p => p.Text).ShouldBe(["what did I ask first?"]);
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
        private readonly Lock _gate = new();

        public List<KeyValuePair<string, string>> Requests { get; } = [];

        public ScriptedHandler On(string key, string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _responses[key] = (body, status);
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
            var key = $"{request.Method} {request.RequestUri!.AbsolutePath}";
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (_gate)
            {
                Requests.Add(new(key, body));
            }

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

        public Task RunShellAsync(string openCodeSessionId, OpenCodeShellRequest request, CancellationToken ct) => Task.CompletedTask;

        public IAsyncEnumerable<OpenCodeSseEvent> SubscribeEvents(string? openCodeSessionId, CancellationToken ct) => AsyncEnumerable.Empty<OpenCodeSseEvent>();

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

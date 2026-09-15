using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;
using WeaveFleet.Infrastructure.Harnesses.OpenCode.Pooling;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode;

/// <summary>
/// <see cref="OpenCodeHarnessSession.AskOffTheRecordAsync"/> asks a throwaway fork so the recap is read from
/// the provider's prompt cache and never lands in the parent's history.
/// </summary>
public sealed class OpenCodeOffTheRecordPromptTests
{
    private const string Directory = "/repo/one";

    [Fact]
    public async Task asks_a_fork_on_the_parents_last_model_and_deletes_the_fork()
    {
        var http = new ScriptedHandler()
            .On("GET /session/oc-1/message", Messages(UserMessage("msg_1", "github-copilot", "claude-sonnet-5", "build", "high"), AssistantMessage("msg_2")))
            .On("GET /session", "[]")
            .On("POST /session/oc-1/fork", """{"id":"fork-1"}""")
            .On("PATCH /session/fork-1", """{"id":"fork-1"}""")
            .On("POST /session/fork-1/message", """{"parts":[{"type":"text","text":"You're adding X. Next, decide Y."}],"info":{"id":"msg_3","role":"assistant"}}""")
            .On("POST /session/fork-1/abort", "true")
            .On("DELETE /session/fork-1", "true");
        await using var session = CreateSession(http);

        var answer = await session.AskOffTheRecordAsync("recap please", CancellationToken.None);

        answer.ShouldBe("You're adding X. Next, decide Y.");
        http.Requests.Select(r => r.Key).ShouldBe([
            "GET /session/oc-1/message",
            "GET /session",
            "POST /session/oc-1/fork",
            "PATCH /session/fork-1",
            "POST /session/fork-1/message",
            "POST /session/fork-1/abort",
            "DELETE /session/fork-1"]);

        using var patch = JsonDocument.Parse(http.Body("PATCH /session/fork-1"));
        patch.RootElement.GetProperty("title").GetString().ShouldBe(OpenCodeHarnessSession.OffTheRecordSessionTitle);
        var rule = patch.RootElement.GetProperty("permission").EnumerateArray().ShouldHaveSingleItem();
        rule.GetProperty("permission").GetString().ShouldBe("*");
        rule.GetProperty("pattern").GetString().ShouldBe("*");
        rule.GetProperty("action").GetString().ShouldBe("ask");

        using var prompt = JsonDocument.Parse(http.Body("POST /session/fork-1/message"));
        var root = prompt.RootElement;
        root.GetProperty("parts")[0].GetProperty("text").GetString().ShouldBe("recap please");
        root.GetProperty("model").GetProperty("providerID").GetString().ShouldBe("github-copilot");
        root.GetProperty("model").GetProperty("modelID").GetString().ShouldBe("claude-sonnet-5");
        root.GetProperty("agent").GetString().ShouldBe("build");
        root.GetProperty("variant").GetString().ShouldBe("high");
        // Switching tools off changes the cached prefix; the fork's ask-all permission stops them instead.
        root.TryGetProperty("tools", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task deletes_the_fork_when_the_answer_fails()
    {
        var http = new ScriptedHandler()
            .On("GET /session/oc-1/message", Messages(UserMessage("msg_1", "openrouter", "anthropic/claude-haiku-4.5")))
            .On("GET /session", "[]")
            .On("POST /session/oc-1/fork", """{"id":"fork-1"}""")
            .On("PATCH /session/fork-1", """{"id":"fork-1"}""")
            .On("POST /session/fork-1/message", "{}", HttpStatusCode.InternalServerError)
            .On("POST /session/fork-1/abort", "true")
            .On("DELETE /session/fork-1", "true");
        await using var session = CreateSession(http);

        await Should.ThrowAsync<HttpRequestException>(() => session.AskOffTheRecordAsync("recap please", CancellationToken.None));

        http.Requests.Select(r => r.Key).ShouldContain("DELETE /session/fork-1");
    }

    [Fact]
    public async Task deletes_the_fork_when_the_caller_gives_up()
    {
        var http = new ScriptedHandler()
            .On("GET /session/oc-1/message", Messages(UserMessage("msg_1", "openrouter", "anthropic/claude-haiku-4.5")))
            .On("GET /session", "[]")
            .On("POST /session/oc-1/fork", """{"id":"fork-1"}""")
            .On("PATCH /session/fork-1", """{"id":"fork-1"}""")
            .Hang("POST /session/fork-1/message")
            .On("POST /session/fork-1/abort", "true")
            .On("DELETE /session/fork-1", "true");
        await using var session = CreateSession(http);
        using var cts = new CancellationTokenSource();

        var ask = session.AskOffTheRecordAsync("recap please", cts.Token);
        await http.WaitForAsync("POST /session/fork-1/message");
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => ask);
        http.Requests.Select(r => r.Key).ShouldContain("DELETE /session/fork-1");
    }

    [Fact]
    public async Task finds_the_last_prompt_on_an_older_page()
    {
        // A long agent turn: the newest page is all assistant steps, the prompt is on the page before.
        // OpenCode pages with its own cursor (X-Next-Cursor); it rejects a message id as "before".
        var steps = Enumerable.Range(0, 50).Select(i => AssistantMessage($"msg_step_{i:D2}")).ToArray();
        var http = new ScriptedHandler()
            .On("GET /session/oc-1/message?before=cursor-older", Messages(UserMessage("msg_0", "openrouter", "anthropic/claude-haiku-4.5")))
            .On("GET /session/oc-1/message", Messages(steps), nextCursor: "cursor-older")
            .On("GET /session", "[]")
            .On("POST /session/oc-1/fork", """{"id":"fork-1"}""")
            .On("PATCH /session/fork-1", """{"id":"fork-1"}""")
            .On("POST /session/fork-1/message", """{"info":{"id":"msg_x","role":"assistant"},"parts":[{"type":"text","text":"Recap."}]}""")
            .On("POST /session/fork-1/abort", "true")
            .On("DELETE /session/fork-1", "true");
        await using var session = CreateSession(http);

        var answer = await session.AskOffTheRecordAsync("recap please", CancellationToken.None);

        answer.ShouldBe("Recap.");
        using var prompt = JsonDocument.Parse(http.Body("POST /session/fork-1/message"));
        prompt.RootElement.GetProperty("model").GetProperty("modelID").GetString().ShouldBe("anthropic/claude-haiku-4.5");
    }

    [Fact]
    public async Task deletes_forks_a_crash_left_behind_but_not_ones_still_in_use()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sessions = JsonSerializer.Serialize(new object[]
        {
            new { id = "old-fork", title = OpenCodeHarnessSession.OffTheRecordSessionTitle, time = new { created = now - 600_000, updated = now } },
            new { id = "busy-fork", title = OpenCodeHarnessSession.OffTheRecordSessionTitle, time = new { created = now - 5_000, updated = now } },
            new { id = "someone-elses", title = "Fix the build", time = new { created = now - 600_000, updated = now } },
        });
        var http = new ScriptedHandler()
            .On("GET /session/oc-1/message", Messages(UserMessage("msg_1", "openrouter", "anthropic/claude-haiku-4.5")))
            .On("GET /session", sessions)
            .On("POST /session/old-fork/abort", "true")
            .On("DELETE /session/old-fork", "true")
            .On("POST /session/oc-1/fork", """{"id":"fork-1"}""")
            .On("PATCH /session/fork-1", """{"id":"fork-1"}""")
            .On("POST /session/fork-1/message", """{"info":{"id":"msg_x","role":"assistant"},"parts":[{"type":"text","text":"Recap."}]}""")
            .On("POST /session/fork-1/abort", "true")
            .On("DELETE /session/fork-1", "true");
        await using var session = CreateSession(http);

        await session.AskOffTheRecordAsync("recap please", CancellationToken.None);

        http.Requests.Select(r => r.Key).Where(k => k.StartsWith("DELETE", StringComparison.Ordinal))
            .ShouldBe(["DELETE /session/old-fork", "DELETE /session/fork-1"]);
    }

    [Fact]
    public async Task returns_null_without_forking_when_the_session_has_no_prompt()
    {
        var http = new ScriptedHandler().On("GET /session/oc-1/message", "[]");
        await using var session = CreateSession(http);

        var answer = await session.AskOffTheRecordAsync("recap please", CancellationToken.None);

        answer.ShouldBeNull();
        http.Requests.Select(r => r.Key).ShouldBe(["GET /session/oc-1/message"]);
    }

    [Fact]
    public async Task returns_null_before_the_session_exists_in_opencode()
    {
        var http = new ScriptedHandler();
        await using var session = CreateSession(http, openCodeSessionId: null);

        (await session.AskOffTheRecordAsync("recap please", CancellationToken.None)).ShouldBeNull();
        http.Requests.ShouldBeEmpty();
    }

    private static OpenCodeHarnessSession CreateSession(ScriptedHandler handler, string? openCodeSessionId = "oc-1")
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234") };
        var handle = new FakeInstanceHandle(new OpenCodeHttpClient(httpClient, NullLogger<OpenCodeHttpClient>.Instance));
        return new OpenCodeHarnessSession(
            instanceId: "test-instance",
            fleetSessionId: "fleet-session-1",
            instanceHandle: handle,
            workingDirectory: Directory,
            scopeFactory: new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            logger: NullLogger<OpenCodeHarnessSession>.Instance,
            ownerUserId: "user-1",
            openCodeSessionId: openCodeSessionId);
    }

    private static string Messages(params string[] messages) => $"[{string.Join(",", messages)}]";

    private static string UserMessage(string id, string providerId, string modelId, string? agent = null, string? variant = null) =>
        JsonSerializer.Serialize(new
        {
            info = new { id, role = "user", sessionID = "oc-1", agent, variant, model = new { providerID = providerId, modelID = modelId }, time = new { created = 1 } },
            parts = new[] { new { id = $"prt_{id}", type = "text", text = "do the thing" } },
        });

    private static string AssistantMessage(string id) =>
        JsonSerializer.Serialize(new
        {
            info = new { id, role = "assistant", sessionID = "oc-1", time = new { created = 2 } },
            parts = Array.Empty<object>(),
        });

    /// <summary>Answers requests by "METHOD /path" (plus "?before=" when paging), recording each one in order.</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (string Body, HttpStatusCode Status, string? NextCursor)> _responses = new(StringComparer.Ordinal);
        private readonly HashSet<string> _hanging = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TaskCompletionSource> _seen = new(StringComparer.Ordinal);
        private readonly Lock _gate = new();

        public List<KeyValuePair<string, string>> Requests { get; } = [];

        public ScriptedHandler On(string key, string body, HttpStatusCode status = HttpStatusCode.OK, string? nextCursor = null)
        {
            _responses[key] = (body, status, nextCursor);
            return this;
        }

        public ScriptedHandler Hang(string key)
        {
            _hanging.Add(key);
            return this;
        }

        public string Body(string key) => Requests.First(r => r.Key == key).Value;

        public Task WaitForAsync(string key) => Seen(key).Task.WaitAsync(TimeSpan.FromSeconds(5));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            query["directory"].ShouldBe(Directory);
            var key = $"{request.Method} {uri.AbsolutePath}" + (query["before"] is { } before ? $"?before={before}" : "");
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (_gate)
            {
                Requests.Add(new(key, body));
            }

            Seen(key).TrySetResult();
            if (_hanging.Contains(key))
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            if (!_responses.TryGetValue(key, out var response))
            {
                throw new InvalidOperationException($"Unscripted request: {key}");
            }

            var message = new HttpResponseMessage(response.Status)
            {
                Content = new StringContent(response.Body, Encoding.UTF8, "application/json"),
            };
            if (response.NextCursor is not null)
                message.Headers.Add("X-Next-Cursor", response.NextCursor);
            return message;
        }

        private TaskCompletionSource Seen(string key)
        {
            lock (_gate)
            {
                if (!_seen.TryGetValue(key, out var tcs))
                {
                    tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _seen[key] = tcs;
                }

                return tcs;
            }
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

        public IAsyncEnumerable<OpenCodeSseEvent> SubscribeEvents(string? openCodeSessionId, CancellationToken ct) => AsyncEnumerable.Empty<OpenCodeSseEvent>();

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

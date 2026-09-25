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

    [Fact]
    public async Task the_recap_fork_of_a_session_without_the_step_tool_denies_it_after_asking_for_everything()
    {
        var http = new ScriptedHandler()
            .On("GET /session/oc-1/message", Messages(UserMessage("msg_1", "github-copilot", "claude-sonnet-5")))
            .On("GET /session", "[]")
            .On("POST /session/oc-1/fork", """{"id":"fork-1"}""")
            .On("PATCH /session/fork-1", """{"id":"fork-1"}""")
            .On("POST /session/fork-1/message", """{"parts":[{"type":"text","text":"Recap."}],"info":{"id":"msg_3","role":"assistant"}}""")
            .On("POST /session/fork-1/abort", "true")
            .On("DELETE /session/fork-1", "true");
        await using var session = CreateSession(http, hideStepTool: true);

        await session.AskOffTheRecordAsync("recap please", CancellationToken.None);

        // OpenCode applies the last matching rule: the deny has to come after the ask, or the fork offers the tool the
        // parent doesn't, and the recap misses the provider's cache.
        using var patch = JsonDocument.Parse(http.Body("PATCH /session/fork-1"));
        var rules = patch.RootElement.GetProperty("permission").EnumerateArray().ToList();
        rules.Select(r => (r.GetProperty("permission").GetString(), r.GetProperty("pattern").GetString(), r.GetProperty("action").GetString()))
            .ShouldBe([("*", "*", "ask"), ("fleet_step_done", "*", "deny")]);
    }

    [Fact]
    public async Task a_session_without_the_step_tool_switches_it_off_on_every_prompt()
    {
        var http = new ScriptedHandler().On("POST /session/oc-1/prompt_async", "");
        await using var session = CreateSession(http, hideStepTool: true);

        await session.SendPromptAsync("hello", null, CancellationToken.None);
        await session.SendPromptAsync("again", null, CancellationToken.None);

        foreach (var body in http.Requests.Where(r => r.Key == "POST /session/oc-1/prompt_async").Select(r => r.Value))
        {
            using var prompt = JsonDocument.Parse(body);
            prompt.RootElement.GetProperty("tools").GetProperty("fleet_step_done").GetBoolean().ShouldBeFalse();
        }
    }

    [Fact]
    public async Task a_workflow_step_keeps_the_step_tool()
    {
        var http = new ScriptedHandler().On("POST /session/oc-1/prompt_async", "");
        await using var session = CreateSession(http);

        await session.SendPromptAsync("hello", null, CancellationToken.None);

        using var prompt = JsonDocument.Parse(http.Body("POST /session/oc-1/prompt_async"));
        prompt.RootElement.TryGetProperty("tools", out _).ShouldBeFalse();
    }

    [Fact]
    public void a_new_session_without_the_step_tool_is_created_with_a_rule_that_denies_it()
    {
        var request = OpenCodeHarnessSession.CreateRequest(hideStepTool: true).ShouldNotBeNull();
        var rule = request.Permission.ShouldNotBeNull().ShouldHaveSingleItem();
        (rule.Permission, rule.Pattern, rule.Action).ShouldBe(("fleet_step_done", "*", "deny"));
        OpenCodeHarnessSession.CreateRequest(hideStepTool: false).ShouldBeNull();
    }

    [Fact]
    public void only_a_process_with_workflows_on_hides_the_step_tool_and_never_from_a_step_or_a_delegated_child()
    {
        var on = new Dictionary<string, string> { ["FLEET_WORKFLOWS"] = "1" };

        OpenCodeHarnessRuntime.HidesStepTool(on, keepsRules: false).ShouldBeTrue();
        // A step keeps the tool; a subagent's child keeps the rules its agent gave it (a tools map would replace them).
        OpenCodeHarnessRuntime.HidesStepTool(on, keepsRules: true).ShouldBeFalse();
        OpenCodeHarnessRuntime.HidesStepTool(new Dictionary<string, string>(), keepsRules: false).ShouldBeFalse();
    }

    [Fact]
    public async Task a_delegated_childs_prompt_carries_no_tools_map()
    {
        // What the runtime builds for a child resumed with DelegatedChild: HideStepTool stays off.
        var http = new ScriptedHandler().On("POST /session/oc-1/prompt_async", "");
        await using var session = CreateSession(http, hideStepTool: OpenCodeHarnessRuntime.HidesStepTool(
            new Dictionary<string, string> { ["FLEET_WORKFLOWS"] = "1" }, keepsRules: true));

        await session.SendPromptAsync("look again", null, CancellationToken.None);

        using var prompt = JsonDocument.Parse(http.Body("POST /session/oc-1/prompt_async"));
        prompt.RootElement.TryGetProperty("tools", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task a_conversation_asks_each_question_on_one_fork_the_recaps_way_reports_tokens_and_deletes_the_fork_once()
    {
        var http = new ScriptedHandler()
            .On("GET /session/oc-1/message", Messages(UserMessage("msg_1", "github-copilot", "claude-sonnet-5", "build", "high")))
            .On("GET /session", "[]")
            .On("POST /session/oc-1/fork", """{"id":"fork-1"}""")
            .On("PATCH /session/fork-1", """{"id":"fork-1"}""")
            .Then("POST /session/fork-1/message", """{"parts":[{"type":"text","text":"name: first"}],"info":{"id":"msg_3","role":"assistant","tokens":{"input":200,"output":300,"reasoning":0,"cache":{"read":3000,"write":0}}}}""")
            .On("POST /session/fork-1/message", """{"parts":[{"type":"text","text":"name: second"}],"info":{"id":"msg_5","role":"assistant","tokens":{"input":150,"output":350,"reasoning":0,"cache":{"read":3500,"write":0}}}}""")
            .On("POST /session/fork-1/abort", "true")
            .On("DELETE /session/fork-1", "true");
        await using var session = CreateSession(http, hideStepTool: true);

        var conversation = (await session.StartOffTheRecordAsync(CancellationToken.None)).ShouldNotBeNull();
        var first = await conversation.AskAsync("draft it", CancellationToken.None);
        var second = await conversation.AskAsync("fix the errors", CancellationToken.None);
        await conversation.DisposeAsync();
        await conversation.DisposeAsync();

        first.ShouldBe(new OffTheRecordAnswer("name: first", new OffTheRecordTokens(3500, 3000)));
        second.ShouldBe(new OffTheRecordAnswer("name: second", new OffTheRecordTokens(4000, 3500)));
        http.Requests.Select(r => r.Key).ShouldBe([
            "GET /session/oc-1/message",
            "GET /session",
            "POST /session/oc-1/fork",
            "PATCH /session/fork-1",
            "POST /session/fork-1/message",
            "POST /session/fork-1/message",
            "POST /session/fork-1/abort",
            "DELETE /session/fork-1"]);

        // The fork is set up as the recap's is, deny rule and all, and both questions are sent the way the recap's is:
        // the parent's model, agent and variant, and no tools map, so the prefix is read from the provider's cache.
        using var patch = JsonDocument.Parse(http.Body("PATCH /session/fork-1"));
        patch.RootElement.GetProperty("permission").EnumerateArray()
            .Select(r => (r.GetProperty("permission").GetString(), r.GetProperty("pattern").GetString(), r.GetProperty("action").GetString()))
            .ShouldBe([("*", "*", "ask"), ("fleet_step_done", "*", "deny")]);
        var prompts = http.Requests.Where(r => r.Key == "POST /session/fork-1/message").Select(r => JsonDocument.Parse(r.Value).RootElement).ToList();
        prompts.Select(p => p.GetProperty("parts")[0].GetProperty("text").GetString()).ShouldBe(["draft it", "fix the errors"]);
        foreach (var prompt in prompts)
        {
            prompt.GetProperty("model").GetProperty("providerID").GetString().ShouldBe("github-copilot");
            prompt.GetProperty("model").GetProperty("modelID").GetString().ShouldBe("claude-sonnet-5");
            prompt.GetProperty("agent").GetString().ShouldBe("build");
            prompt.GetProperty("variant").GetString().ShouldBe("high");
            prompt.TryGetProperty("tools", out _).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task a_conversation_whose_question_is_cancelled_still_deletes_its_fork()
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

        var conversation = (await session.StartOffTheRecordAsync(CancellationToken.None)).ShouldNotBeNull();
        await using (conversation)
        {
            var ask = conversation.AskAsync("draft it", cts.Token);
            await http.WaitForAsync("POST /session/fork-1/message");
            await cts.CancelAsync();
            await Should.ThrowAsync<OperationCanceledException>(() => ask);
        }

        http.Requests.Select(r => r.Key).TakeLast(2).ShouldBe(["POST /session/fork-1/abort", "DELETE /session/fork-1"]);
    }

    [Fact]
    public async Task a_fork_that_cant_be_set_up_is_deleted()
    {
        var http = new ScriptedHandler()
            .On("GET /session/oc-1/message", Messages(UserMessage("msg_1", "openrouter", "anthropic/claude-haiku-4.5")))
            .On("GET /session", "[]")
            .On("POST /session/oc-1/fork", """{"id":"fork-1"}""")
            .On("PATCH /session/fork-1", "{}", HttpStatusCode.InternalServerError)
            .On("POST /session/fork-1/abort", "true")
            .On("DELETE /session/fork-1", "true");
        await using var session = CreateSession(http);

        await Should.ThrowAsync<HttpRequestException>(() => session.StartOffTheRecordAsync(CancellationToken.None));

        http.Requests.Select(r => r.Key).ShouldContain("DELETE /session/fork-1");
    }

    private static OpenCodeHarnessSession CreateSession(ScriptedHandler handler, string? openCodeSessionId = "oc-1", bool hideStepTool = false)
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
            openCodeSessionId: openCodeSessionId)
        {
            HideStepTool = hideStepTool,
        };
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
        private readonly Dictionary<string, Queue<string>> _then = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TaskCompletionSource> _seen = new(StringComparer.Ordinal);
        private readonly Lock _gate = new();

        public List<KeyValuePair<string, string>> Requests { get; } = [];

        public ScriptedHandler On(string key, string body, HttpStatusCode status = HttpStatusCode.OK, string? nextCursor = null)
        {
            _responses[key] = (body, status, nextCursor);
            return this;
        }

        /// <summary>Answers the next request to <paramref name="key"/> with <paramref name="body"/>, before the one <see cref="On"/> gave.</summary>
        public ScriptedHandler Then(string key, string body)
        {
            if (!_then.TryGetValue(key, out var queue))
                _then[key] = queue = new Queue<string>();
            queue.Enqueue(body);
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

            if (_then.TryGetValue(key, out var queued) && queued.TryDequeue(out var next))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(next, Encoding.UTF8, "application/json") };
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

        public Task RunShellAsync(string openCodeSessionId, OpenCodeShellRequest request, CancellationToken ct) =>
            HttpClient.RunShellAsync(openCodeSessionId, request, Directory, ct);

        public IAsyncEnumerable<OpenCodeSseEvent> SubscribeEvents(string? openCodeSessionId, CancellationToken ct) => AsyncEnumerable.Empty<OpenCodeSseEvent>();

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

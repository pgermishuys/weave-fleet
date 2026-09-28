using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;
using WeaveFleet.Infrastructure.Harnesses.OpenCode.Pooling;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode;

/// <summary>
/// An OpenCode session's asks. OpenCode asks for everything but reading (Fleet's config), and the session decides each
/// for its permission level: allowed at once, or put to the user as Fleet's <c>permission.asked</c>.
/// </summary>
public sealed class OpenCodePermissionTests
{
    private const string Directory = "/repo/one";

    [Fact]
    public async Task An_ask_the_level_doesnt_allow_is_shown_the_refusal_carries_the_users_words_and_the_reply_settles_it()
    {
        var (http, events, session) = Start();
        await using var _ = session;
        await session.ApplyPermissionsAsync(new PermissionPolicy(PermissionLevels.Ask), CancellationToken.None);
        var stream = session.SubscribeAsync(CancellationToken.None).GetAsyncEnumerator();

        await events.Writer.WriteAsync(Asked("per_1", "oc-1", "bash", "git push origin main", "git push *"));

        var asked = await NextAsync(stream);
        asked.Type.ShouldBe(EventTypes.PermissionAsked);
        var ask = asked.Payload!.Value;
        ask.GetProperty("sessionId").GetString().ShouldBe("fleet-session-1");
        ask.GetProperty("kind").GetString().ShouldBe(PermissionKinds.Shell);
        ask.GetProperty("title").GetString().ShouldBe("git push origin main");
        ask.GetProperty("callId").GetString().ShouldBe("call_1");
        ask.TryGetProperty("subagent", out var subagent).ShouldBeFalse(subagent.ToString());
        Status(await NextAsync(stream)).ShouldBe(ActivityStatuses.WaitingInput);

        await session.ReplyToPermissionAsync("per_1", PermissionReplies.Reject, "Push after review", CancellationToken.None);

        var body = JsonDocument.Parse(http.Bodies("POST /permission/per_1/reply").Single()).RootElement;
        body.GetProperty("reply").GetString().ShouldBe("reject");
        body.GetProperty("message").GetString().ShouldBe("Push after review");

        await events.Writer.WriteAsync(Sse("permission.replied", """{"sessionID":"oc-1","requestID":"per_1","reply":"reject"}"""));
        (await NextAsync(stream)).Type.ShouldBe(EventTypes.PermissionReplied);
        Status(await NextAsync(stream)).ShouldBe(ActivityStatuses.Busy);
    }

    [Fact]
    public async Task An_ask_the_level_allows_is_answered_once_and_goes_no_further()
    {
        var (http, events, session) = Start();
        await using var _ = session;
        await session.ApplyPermissionsAsync(new PermissionPolicy(PermissionLevels.Edits), CancellationToken.None);
        var stream = session.SubscribeAsync(CancellationToken.None).GetAsyncEnumerator();

        await events.Writer.WriteAsync(Asked("per_1", "oc-1", "edit", "src/app.ts", "*"));
        await events.Writer.WriteAsync(Sse("session.idle", """{"sessionID":"oc-1"}"""));

        (await NextAsync(stream)).Type.ShouldBe(EventTypes.SessionIdle);
        await WaitForAsync(() => http.Bodies("POST /permission/per_1/reply").Count == 1);
        JsonDocument.Parse(http.Bodies("POST /permission/per_1/reply").Single()).RootElement.GetProperty("reply").GetString().ShouldBe("once");
    }

    [Fact]
    public async Task A_subagents_ask_that_reaches_the_parent_is_decided_and_shown_by_it()
    {
        var (_, events, session) = Start();
        await using var _ = session;
        await session.ApplyPermissionsAsync(new PermissionPolicy(PermissionLevels.Ask), CancellationToken.None);
        var stream = session.SubscribeAsync(CancellationToken.None).GetAsyncEnumerator();

        await events.Writer.WriteAsync(Asked("per_2", "oc-child", "webfetch", "https://example.com", "*"));

        var asked = await NextAsync(stream);
        asked.Type.ShouldBe(EventTypes.PermissionAsked);
        asked.FleetSessionId.ShouldBeNull();
        asked.Payload!.Value.GetProperty("subagent").GetString().ShouldBe("subagent");
        asked.Payload!.Value.GetProperty("kind").GetString().ShouldBe(PermissionKinds.Web);
    }

    [Fact]
    public async Task A_turn_that_ends_takes_its_asks_with_it()
    {
        var (_, events, session) = Start();
        await using var _ = session;
        await session.ApplyPermissionsAsync(new PermissionPolicy(PermissionLevels.Ask), CancellationToken.None);
        var stream = session.SubscribeAsync(CancellationToken.None).GetAsyncEnumerator();
        await events.Writer.WriteAsync(Asked("per_1", "oc-1", "bash", "sleep 100", "sleep *"));
        await NextAsync(stream);
        await NextAsync(stream);

        await events.Writer.WriteAsync(Sse("session.idle", """{"sessionID":"oc-1"}"""));

        var gone = await NextAsync(stream);
        gone.Type.ShouldBe(EventTypes.PermissionReplied);
        gone.Payload!.Value.GetProperty("reply").GetString().ShouldBe(PermissionReplies.Gone);
        await Should.ThrowAsync<KeyNotFoundException>(
            () => session.ReplyToPermissionAsync("per_1", PermissionReplies.Once, null, CancellationToken.None));
    }

    private static (RecordingHandler Http, Channel<OpenCodeSseEvent> Events, OpenCodeHarnessSession Session) Start()
    {
        var http = new RecordingHandler();
        var events = Channel.CreateUnbounded<OpenCodeSseEvent>();
        var httpClient = new HttpClient(http) { BaseAddress = new Uri("http://localhost:1234") };
        var session = new OpenCodeHarnessSession(
            instanceId: "test-instance",
            fleetSessionId: "fleet-session-1",
            instanceHandle: new StreamingInstanceHandle(new OpenCodeHttpClient(httpClient, NullLogger<OpenCodeHttpClient>.Instance), events),
            workingDirectory: Directory,
            scopeFactory: new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            logger: NullLogger<OpenCodeHarnessSession>.Instance,
            ownerUserId: "user-1",
            openCodeSessionId: "oc-1");
        return (http, events, session);
    }

    private static OpenCodeSseEvent Asked(string id, string sessionId, string permission, string pattern, string always)
        => Sse("permission.asked", JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = id,
            ["sessionID"] = sessionId,
            ["permission"] = permission,
            ["patterns"] = new[] { pattern },
            ["always"] = new[] { always },
            ["metadata"] = permission == "bash" ? new Dictionary<string, object?> { ["command"] = pattern } : new Dictionary<string, object?>(),
            ["tool"] = new Dictionary<string, object?> { ["messageID"] = "msg_1", ["callID"] = "call_1" },
        }));

    private static OpenCodeSseEvent Sse(string type, string properties)
        => new() { Type = type, Properties = JsonDocument.Parse(properties).RootElement.Clone() };

    private static string? Status(HarnessEvent evt)
    {
        evt.Type.ShouldBe(EventTypes.SessionStatus);
        return evt.Payload!.Value.GetProperty("status").GetProperty("type").GetString();
    }

    private static async Task<HarnessEvent> NextAsync(IAsyncEnumerator<HarnessEvent> stream)
    {
        (await stream.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue();
        return stream.Current;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The condition never held.");
            await Task.Delay(20);
        }
    }

    /// <summary>Answers every request with <c>true</c> and keeps its body by "METHOD /path".</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Lock _gate = new();
        private readonly List<KeyValuePair<string, string>> _requests = [];

        public List<string> Bodies(string key)
        {
            lock (_gate)
                return [.. _requests.Where(r => r.Key == key).Select(r => r.Value)];
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (_gate)
                _requests.Add(new($"{request.Method} {request.RequestUri!.AbsolutePath}", body));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("true", Encoding.UTF8, "application/json") };
        }
    }

    private sealed class StreamingInstanceHandle(OpenCodeHttpClient httpClient, Channel<OpenCodeSseEvent> events) : IOpenCodeInstanceHandle
    {
        public event EventHandler<int>? ProcessExited { add { } remove { } }

        public OpenCodeHttpClient HttpClient { get; } = httpClient;

        public int? ProcessId => null;

        public bool IsRunning => true;

        public Task EnsureConnectedAsync(CancellationToken ct) => Task.CompletedTask;

        public Task WaitForEventSubscriptionAsync(string openCodeSessionId, CancellationToken ct) => Task.CompletedTask;

        public Task SendCommandAsync(string openCodeSessionId, OpenCodeCommandRequest request, CancellationToken ct) => Task.CompletedTask;

        public Task RunShellAsync(string openCodeSessionId, OpenCodeShellRequest request, CancellationToken ct) => Task.CompletedTask;

        public IAsyncEnumerable<OpenCodeSseEvent> SubscribeEvents(string? openCodeSessionId, CancellationToken ct) => events.Reader.ReadAllAsync(ct);

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

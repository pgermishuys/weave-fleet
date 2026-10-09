using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;
using WeaveFleet.Infrastructure.Harnesses.OpenCode.Pooling;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode;

/// <summary>
/// Answering and dismissing the agent's questions on OpenCode (1.x): <c>POST /question/{id}/reply</c> and
/// <c>/reject</c>. A question that isn't waiting is <see cref="KeyNotFoundException"/>, as for permission asks.
/// </summary>
public sealed class OpenCodeQuestionTests
{
    private const string Directory = "/repo/one";

    /// <summary>What OpenCode 1.18.33 answers for a question id it has no pending question for.</summary>
    private const string QuestionNotFound = """{"_tag":"QuestionNotFoundError","requestID":"que_gone","message":"Question request not found: que_gone"}""";

    [Fact]
    public async Task An_answer_goes_to_the_question()
    {
        var http = new ScriptedHandler().On("POST /question/que_1/reply", "true");
        await using var session = CreateSession(http);

        await session.AnswerQuestionAsync("que_1", [["Staging"]], CancellationToken.None);

        http.Body("POST /question/que_1/reply").ShouldBe("""{"answers":[["Staging"]]}""");
    }

    [Fact]
    public async Task An_answer_to_a_question_opencode_no_longer_has_is_not_found()
    {
        var http = new ScriptedHandler().On("POST /question/que_gone/reply", QuestionNotFound, HttpStatusCode.NotFound);
        await using var session = CreateSession(http);

        await Should.ThrowAsync<KeyNotFoundException>(() => session.AnswerQuestionAsync("que_gone", [["Staging"]], CancellationToken.None));
    }

    [Fact]
    public async Task Dismissing_a_question_opencode_no_longer_has_is_not_found()
    {
        var http = new ScriptedHandler().On("POST /question/que_gone/reject", QuestionNotFound, HttpStatusCode.NotFound);
        await using var session = CreateSession(http);

        await Should.ThrowAsync<KeyNotFoundException>(() => session.RejectQuestionAsync("que_gone", CancellationToken.None));
    }

    [Fact]
    public async Task A_tool_call_that_asked_no_question_is_not_found_without_asking_opencode()
    {
        // OpenCode refuses an id that isn't a question's (que…) with 400 before looking for it.
        var http = new ScriptedHandler()
            .On("POST /question/call_gone/reply", """{"_tag":"BadRequest"}""", HttpStatusCode.BadRequest)
            .On("POST /question/call_gone/reject", """{"_tag":"BadRequest"}""", HttpStatusCode.BadRequest);
        await using var session = CreateSession(http);

        await Should.ThrowAsync<KeyNotFoundException>(() => session.AnswerQuestionAsync("call_gone", [["Staging"]], CancellationToken.None));
        await Should.ThrowAsync<KeyNotFoundException>(() => session.RejectQuestionAsync("call_gone", CancellationToken.None));
        http.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Any_other_failure_stays_an_error(HttpStatusCode status)
    {
        var http = new ScriptedHandler()
            .On("POST /question/que_1/reply", "{}", status)
            .On("POST /question/que_1/reject", "{}", status);
        await using var session = CreateSession(http);

        (await Should.ThrowAsync<HttpRequestException>(() => session.AnswerQuestionAsync("que_1", [["Staging"]], CancellationToken.None)))
            .StatusCode.ShouldBe(status);
        (await Should.ThrowAsync<HttpRequestException>(() => session.RejectQuestionAsync("que_1", CancellationToken.None)))
            .StatusCode.ShouldBe(status);
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
            var uri = request.RequestUri!;
            System.Web.HttpUtility.ParseQueryString(uri.Query)["directory"].ShouldBe(Directory);
            var key = $"{request.Method} {uri.AbsolutePath}";
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

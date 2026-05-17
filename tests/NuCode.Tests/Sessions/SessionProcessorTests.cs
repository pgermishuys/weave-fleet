using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NuCode.Audit;
using NuCode.Events;
using NuCode.Fakes;
using NuCode.Sessions;

namespace NuCode;

public sealed class SessionProcessorTests : IDisposable
{
    private readonly SqliteSessionStore _store;
    private readonly NuCodeEventBus _eventBus;
    private readonly SessionService _sessionService;
    private readonly SessionProcessor _processor;

    public SessionProcessorTests()
    {
        _store = new SqliteSessionStore("Data Source=:memory:");
        _eventBus = new NuCodeEventBus();
        _sessionService = new SessionService(_store, _eventBus);
        _processor = new SessionProcessor(
            _sessionService, NullLogger<SessionProcessor>.Instance,
            new AuditEventSubscriber(new NullAuditService(), _eventBus));
    }

    public void Dispose() => _store.Dispose();

    // ── Helpers ──

    private async Task<(NuCodeSession Session, AssistantMessage Message, NuCodeAgentSession AgentSession)>
        SetupSessionAsync()
    {
        var session = await _sessionService.CreateSessionAsync("/workspace", "Test", CancellationToken.None);
        var userMsg = new UserMessage(
            MessageId.New(), session.Id, DateTimeOffset.UtcNow, "build");
        await _sessionService.UpsertMessageAsync(userMsg, CancellationToken.None);

        var assistantMsg = new AssistantMessage(
            MessageId.New(), session.Id, DateTimeOffset.UtcNow,
            ParentId: userMsg.Id, Agent: "build",
            ProviderId: "test", ModelId: "test-model");
        await _sessionService.UpsertMessageAsync(assistantMsg, CancellationToken.None);

        var agentSession = new NuCodeAgentSession(session);

        return (session, assistantMsg, agentSession);
    }

    private static AgentResponseUpdate TextUpdate(string text)
    {
        return new AgentResponseUpdate
        {
            Contents = [new TextContent(text)],
        };
    }

    private static AgentResponseUpdate FinishUpdate(ChatFinishReason reason)
    {
        return new AgentResponseUpdate
        {
            FinishReason = reason,
        };
    }

    private static AgentResponseUpdate FunctionCallUpdate(string callId, string name, IDictionary<string, object?>? args = null)
    {
        return new AgentResponseUpdate
        {
            Contents = [new FunctionCallContent(callId, name, args)],
        };
    }

    private static AgentResponseUpdate FunctionResultUpdate(string callId, object? result = null, Exception? exception = null)
    {
        return new AgentResponseUpdate
        {
            Contents = [new FunctionResultContent(callId, result) { Exception = exception }],
        };
    }

    private static async IAsyncEnumerable<AgentResponseUpdate> ToStream(
        params AgentResponseUpdate[] updates)
    {
        foreach (var update in updates)
        {
            await Task.Yield();
            yield return update;
        }
    }

    private static async IAsyncEnumerable<AgentResponseUpdate> ThrowingStream(
        Exception exception)
    {
        await Task.Yield();
        throw exception;
#pragma warning disable CS0162 // Unreachable code detected — required to satisfy IAsyncEnumerable<T> return type
        yield break;
#pragma warning restore CS0162
    }

    private static async IAsyncEnumerable<AgentResponseUpdate> CancellingStream(
        AgentResponseUpdate[] updates,
        CancellationTokenSource cts,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var update in updates)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return update;
            cts.Cancel();
        }
    }

    // ── Text streaming ──

    [Fact]
    public async Task TextOnlyStreamCreatesTextPartAndReturnsStop()
    {
        var (session, assistantMsg, agentSession) = await SetupSessionAsync();

        var stream = ToStream(
            TextUpdate("Hello "),
            TextUpdate("world!"),
            FinishUpdate(ChatFinishReason.Stop));

        var result = await _processor.ProcessStreamAsync(
            stream, assistantMsg, agentSession, CancellationToken.None);

        result.ShouldBe(ProcessResult.Stop);

        // Verify text part was created and finalized
        var messages = await _sessionService.GetMessagesAsync(session.Id, CancellationToken.None);
        var assistantParts = messages
            .First(m => m.Message.Id == assistantMsg.Id).Parts;

        var textPart = assistantParts.OfType<TextPart>().ShouldHaveSingleItem();
        textPart.Text.ShouldBe("Hello world!");
        textPart.StartTime.ShouldNotBeNull();
        textPart.EndTime.ShouldNotBeNull();
    }

    [Fact]
    public async Task EmptyTextDeltasAreIgnored()
    {
        var (session, assistantMsg, agentSession) = await SetupSessionAsync();

        var stream = ToStream(
            TextUpdate(""),
            TextUpdate("actual text"),
            FinishUpdate(ChatFinishReason.Stop));

        var result = await _processor.ProcessStreamAsync(
            stream, assistantMsg, agentSession, CancellationToken.None);

        result.ShouldBe(ProcessResult.Stop);

        var messages = await _sessionService.GetMessagesAsync(session.Id, CancellationToken.None);
        var assistantParts = messages
            .First(m => m.Message.Id == assistantMsg.Id).Parts;

        var textPart = assistantParts.OfType<TextPart>().ShouldHaveSingleItem();
        textPart.Text.ShouldBe("actual text");
    }

    [Fact]
    public async Task NoContentReturnsStop()
    {
        var (_, assistantMsg, agentSession) = await SetupSessionAsync();

        var stream = ToStream(FinishUpdate(ChatFinishReason.Stop));

        var result = await _processor.ProcessStreamAsync(
            stream, assistantMsg, agentSession, CancellationToken.None);

        result.ShouldBe(ProcessResult.Stop);
    }

    // ── Streaming delta events ──

    [Fact]
    public async Task TextDeltaEventsArePublished()
    {
        var (_, assistantMsg, agentSession) = await SetupSessionAsync();

        var deltas = new List<string>();
        using var sub = _eventBus.Subscribe(MessageEvents.PartDeltaReceived,
            e => deltas.Add(e.Properties.Delta));

        var stream = ToStream(
            TextUpdate("Hello "),
            TextUpdate("world!"),
            FinishUpdate(ChatFinishReason.Stop));

        await _processor.ProcessStreamAsync(
            stream, assistantMsg, agentSession, CancellationToken.None);

        deltas.Count.ShouldBe(2);
        deltas[0].ShouldBe("Hello ");
        deltas[1].ShouldBe("world!");
    }

    // ── Finish reason handling ──

    [Fact]
    public async Task FinishReasonIsPersistedOnMessage()
    {
        var (session, assistantMsg, agentSession) = await SetupSessionAsync();

        var stream = ToStream(
            TextUpdate("Done"),
            FinishUpdate(ChatFinishReason.Stop));

        await _processor.ProcessStreamAsync(
            stream, assistantMsg, agentSession, CancellationToken.None);

        var messages = await _sessionService.GetMessagesAsync(session.Id, CancellationToken.None);
        var updatedMsg = messages
            .Select(m => m.Message)
            .OfType<AssistantMessage>()
            .First(m => m.Id == assistantMsg.Id);

        updatedMsg.CompletedAt.ShouldNotBeNull();
        updatedMsg.FinishReason.ShouldBe("stop");
    }

    // ── Session status transitions ──

    [Fact]
    public async Task SessionStatusTransitionsBusyThenIdle()
    {
        var (_, assistantMsg, agentSession) = await SetupSessionAsync();

        var statusChecker = new StatusCheckingAsyncEnumerable(
            [TextUpdate("text"), FinishUpdate(ChatFinishReason.Stop)],
            agentSession);

        await _processor.ProcessStreamAsync(
            statusChecker, assistantMsg, agentSession, CancellationToken.None);

        statusChecker.WasBusyDuringStream.ShouldBeTrue("Status should have been Busy during streaming");
        agentSession.Status.ShouldBeOfType<IdleSessionStatus>();
    }

    private sealed class StatusCheckingAsyncEnumerable : IAsyncEnumerable<AgentResponseUpdate>
    {
        private readonly AgentResponseUpdate[] _updates;
        private readonly NuCodeAgentSession _agentSession;

        public bool WasBusyDuringStream { get; private set; }

        public StatusCheckingAsyncEnumerable(
            AgentResponseUpdate[] updates,
            NuCodeAgentSession agentSession)
        {
            _updates = updates;
            _agentSession = agentSession;
        }

        public async IAsyncEnumerator<AgentResponseUpdate> GetAsyncEnumerator(
            CancellationToken cancellationToken)
        {
            // Check on first MoveNext — processor already set Busy before iterating
            WasBusyDuringStream = _agentSession.Status is BusySessionStatus;

            foreach (var update in _updates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return update;
            }
        }
    }

    // ── Cancellation ──

    [Fact]
    public async Task CancellationSetsAbortedErrorAndReturnsStop()
    {
        var (session, assistantMsg, agentSession) = await SetupSessionAsync();

        using var cts = new CancellationTokenSource();
        var stream = CancellingStream(
            [TextUpdate("partial"), TextUpdate(" more")],
            cts);

        var result = await _processor.ProcessStreamAsync(
            stream, assistantMsg, agentSession, cts.Token);

        result.ShouldBe(ProcessResult.Stop);

        var messages = await _sessionService.GetMessagesAsync(session.Id, CancellationToken.None);
        var updatedMsg = messages
            .Select(m => m.Message)
            .OfType<AssistantMessage>()
            .First(m => m.Id == assistantMsg.Id);

        updatedMsg.Error.ShouldBeOfType<AbortedError>();
    }

    // ── Error classification ──

    [Fact]
    public async Task ContextOverflowErrorTriggersCompaction()
    {
        var (session, assistantMsg, agentSession) = await SetupSessionAsync();

        var stream = ThrowingStream(
            new InvalidOperationException("context_length_exceeded: maximum context length is 200000 tokens"));

        var result = await _processor.ProcessStreamAsync(
            stream, assistantMsg, agentSession, CancellationToken.None);

        result.ShouldBe(ProcessResult.Compact);

        var messages = await _sessionService.GetMessagesAsync(session.Id, CancellationToken.None);
        var updatedMsg = messages
            .Select(m => m.Message)
            .OfType<AssistantMessage>()
            .First(m => m.Id == assistantMsg.Id);

        updatedMsg.Error.ShouldBeOfType<ContextOverflowError>();
    }

    [Fact]
    public async Task RateLimitErrorIsClassifiedAsRetryable()
    {
        var (session, assistantMsg, agentSession) = await SetupSessionAsync();

        var stream = ThrowingStream(
            new InvalidOperationException("429 rate_limit_exceeded"));

        var result = await _processor.ProcessStreamAsync(
            stream, assistantMsg, agentSession, CancellationToken.None);

        result.ShouldBe(ProcessResult.Stop);

        var messages = await _sessionService.GetMessagesAsync(session.Id, CancellationToken.None);
        var updatedMsg = messages
            .Select(m => m.Message)
            .OfType<AssistantMessage>()
            .First(m => m.Id == assistantMsg.Id);

        var apiError = updatedMsg.Error.ShouldBeOfType<ApiError>();
        apiError.IsRetryable.ShouldBeTrue();
    }

    [Fact]
    public async Task AuthenticationErrorIsClassified()
    {
        var (session, assistantMsg, agentSession) = await SetupSessionAsync();

        var stream = ThrowingStream(
            new InvalidOperationException("unauthorized: invalid api_key"));

        var result = await _processor.ProcessStreamAsync(
            stream, assistantMsg, agentSession, CancellationToken.None);

        result.ShouldBe(ProcessResult.Stop);

        var messages = await _sessionService.GetMessagesAsync(session.Id, CancellationToken.None);
        var updatedMsg = messages
            .Select(m => m.Message)
            .OfType<AssistantMessage>()
            .First(m => m.Id == assistantMsg.Id);

        updatedMsg.Error.ShouldBeOfType<ProviderAuthError>();
    }

    [Fact]
    public async Task OutputLengthErrorIsClassified()
    {
        var (_, assistantMsg, agentSession) = await SetupSessionAsync();

        var stream = ThrowingStream(
            new InvalidOperationException("max_tokens exceeded output length"));

        var result = await _processor.ProcessStreamAsync(
            stream, assistantMsg, agentSession, CancellationToken.None);

        result.ShouldBe(ProcessResult.Stop);
    }

    [Fact]
    public async Task UnknownErrorIsClassifiedAndReturnsStop()
    {
        var (session, assistantMsg, agentSession) = await SetupSessionAsync();

        var stream = ThrowingStream(
            new InvalidOperationException("something unexpected happened"));

        var result = await _processor.ProcessStreamAsync(
            stream, assistantMsg, agentSession, CancellationToken.None);

        result.ShouldBe(ProcessResult.Stop);

        var messages = await _sessionService.GetMessagesAsync(session.Id, CancellationToken.None);
        var updatedMsg = messages
            .Select(m => m.Message)
            .OfType<AssistantMessage>()
            .First(m => m.Id == assistantMsg.Id);

        updatedMsg.Error.ShouldBeOfType<UnknownMessageError>();
    }

    // ── Tool call flow ──

    [Fact]
    public async Task ToolCallFlowCreatesToolPartAndReturnsContinue()
    {
        var (session, assistantMsg, agentSession) = await SetupSessionAsync();

        var callId = "call_001";
        var args = new Dictionary<string, object?> { ["message"] = "hello" };

        // Simulate: function call → function result → stop
        var stream = ToStream(
            FunctionCallUpdate(callId, "echo", args),
            FunctionCallUpdate(callId, "echo", args),  // Second call transitions pending → running
            FunctionResultUpdate(callId, "echoed: hello"),
            FinishUpdate(ChatFinishReason.Stop));

        var result = await _processor.ProcessStreamAsync(
            stream, assistantMsg, agentSession, CancellationToken.None);

        // Tool calls completed → should continue
        result.ShouldBe(ProcessResult.Continue);

        // Verify tool part was created
        var messages = await _sessionService.GetMessagesAsync(session.Id, CancellationToken.None);
        var assistantParts = messages
            .First(m => m.Message.Id == assistantMsg.Id).Parts;

        var toolParts = assistantParts.OfType<ToolPart>().ToList();
        toolParts.ShouldHaveSingleItem();
        toolParts[0].ToolName.ShouldBe("echo");

        // Should be in completed state
        var completedState = toolParts[0].State.ShouldBeOfType<CompletedToolCallState>();
        completedState.Output.ShouldBe("echoed: hello");
    }

    // ── Status restored to idle after error ──

    [Fact]
    public async Task SessionStatusRestoredToIdleAfterError()
    {
        var (_, assistantMsg, agentSession) = await SetupSessionAsync();

        var stream = ThrowingStream(
            new InvalidOperationException("test error"));

        await _processor.ProcessStreamAsync(
            stream, assistantMsg, agentSession, CancellationToken.None);

        agentSession.Status.ShouldBeOfType<IdleSessionStatus>();
    }

    // ── Tool call with finish_reason "tool_calls" ──

    [Fact]
    public async Task FinishReasonToolCallsReturnsContinue()
    {
        var (_, assistantMsg, agentSession) = await SetupSessionAsync();

        var stream = ToStream(
            TextUpdate("Let me use a tool."),
            new AgentResponseUpdate { FinishReason = new ChatFinishReason("tool_calls") });

        var result = await _processor.ProcessStreamAsync(
            stream, assistantMsg, agentSession, CancellationToken.None);

        result.ShouldBe(ProcessResult.Continue);
    }
}

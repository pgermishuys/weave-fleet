using System.Text.Json;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.TestHarness;

/// <summary>
/// A configured test scenario that defines the mock harness behaviour for a single test.
/// </summary>
public sealed class TestScenario
{
    /// <summary>Pre-loaded messages returned by <c>GetMessagesAsync</c>.</summary>
    public IReadOnlyList<HarnessMessage> Messages { get; init; } = [];

    /// <summary>
    /// Sequences of events to emit when a prompt is received.
    /// Each call to <c>SendPromptAsync</c> dequeues the next response sequence.
    /// </summary>
    public Queue<IReadOnlyList<ScenarioEvent>> PromptResponses { get; init; } = new();

    /// <summary>
    /// When true, <c>SpawnAsync</c> throws <see cref="InvalidOperationException"/>.
    /// </summary>
    public bool ThrowOnSpawn { get; init; }

    /// <summary>
    /// When true, <c>SendPromptAsync</c> throws <see cref="InvalidOperationException"/>.
    /// </summary>
    public bool ThrowOnSendPrompt { get; init; }

    /// <summary>
    /// Initial lifecycle status of the spawned instance. Defaults to <see cref="HarnessSessionStatus.Idle"/>.
    /// </summary>
    public HarnessSessionStatus InitialStatus { get; init; } = HarnessSessionStatus.Idle;
}

/// <summary>
/// A single event in a scenario response sequence.
/// </summary>
public sealed record ScenarioEvent
{
    /// <summary>The harness event to emit.</summary>
    public required HarnessEvent Event { get; init; }

    /// <summary>Optional delay before emitting this event (simulates streaming latency).</summary>
    public TimeSpan Delay { get; init; } = TimeSpan.Zero;
}

/// <summary>
/// Fluent builder for <see cref="TestScenario"/>.
/// </summary>
public sealed class TestScenarioBuilder
{
    private readonly List<HarnessMessage> _messages = [];
    private readonly Queue<IReadOnlyList<ScenarioEvent>> _promptResponses = new();
    private bool _throwOnSpawn;
    private bool _throwOnSendPrompt;
    private HarnessSessionStatus _initialStatus = HarnessSessionStatus.Idle;

    // ── Pre-loaded messages ──────────────────────────────────────────────────

    /// <summary>Add a pre-loaded user message to the scenario.</summary>
    public TestScenarioBuilder WithUserMessage(string id, string text, DateTimeOffset? timestamp = null)
    {
        _messages.Add(new HarnessMessage
        {
            Id = id,
            Role = "user",
            Parts = [new TextPart(text)],
            Timestamp = timestamp ?? DateTimeOffset.UtcNow
        });
        return this;
    }

    /// <summary>Add a pre-loaded assistant message to the scenario.</summary>
    public TestScenarioBuilder WithAssistantMessage(
        string id,
        string text,
        DateTimeOffset? timestamp = null)
    {
        _messages.Add(new HarnessMessage
        {
            Id = id,
            Role = "assistant",
            Parts = [new TextPart(text)],
            Timestamp = timestamp ?? DateTimeOffset.UtcNow
        });
        return this;
    }

    /// <summary>Add a pre-loaded assistant message with custom parts.</summary>
    public TestScenarioBuilder WithAssistantMessageParts(
        string id,
        IReadOnlyList<MessagePart> parts,
        DateTimeOffset? timestamp = null)
    {
        _messages.Add(new HarnessMessage
        {
            Id = id,
            Role = "assistant",
            Parts = parts,
            Timestamp = timestamp ?? DateTimeOffset.UtcNow
        });
        return this;
    }

    // ── Prompt responses ─────────────────────────────────────────────────────

    /// <summary>
    /// Enqueue a response for the next prompt. The events will be emitted in order
    /// via <c>SubscribeAsync</c> when the harness receives a prompt.
    /// </summary>
    public TestScenarioBuilder WithPromptResponse(
        Action<PromptResponseBuilder> configure)
    {
        var builder = new PromptResponseBuilder();
        configure(builder);
        _promptResponses.Enqueue(builder.Build());
        return this;
    }

    /// <summary>
    /// Enqueue a simple text response for the next prompt.
    /// Emits: session.status(busy) → message.updated → message.part.updated → session.idle
    /// Payload structure matches the OpenCode SSE contract so the frontend can parse them.
    /// See tests/contracts/opencode-to-fleet-events.json for the canonical format.
    /// </summary>
    public TestScenarioBuilder WithSimpleTextResponse(
        string sessionId,
        string messageId,
        string text,
        TimeSpan? delay = null)
    {
        var userMessageId = $"{messageId}-user";
        var userPartId = $"{userMessageId}-part-1";
        var partId = $"{messageId}-part-1";
        var responseDelay = delay ?? TimeSpan.FromMilliseconds(10);

        return WithPromptResponse(b => b
            .AddEvent(MakeEvent(sessionId, "session.status",
                new { sessionId, status = new { type = "busy" } }))
            // User message — mirrors real OpenCode behaviour where the user's prompt
            // is echoed back via SSE so the frontend can render it.
            .AddEvent(MakeEvent(sessionId, "message.updated",
                new { info = new { id = userMessageId, sessionID = sessionId, role = "user" } }),
                responseDelay)
            .AddEvent(MakeEvent(sessionId, "message.part.updated",
                new { sessionID = sessionId, part = new { id = userPartId, sessionID = sessionId, messageID = userMessageId, type = "text", text = "_user_prompt_" } }),
                responseDelay)
            // Assistant response
            .AddEvent(MakeEvent(sessionId, "message.updated",
                new { info = new { id = messageId, sessionID = sessionId, role = "assistant" } }),
                responseDelay)
            .AddEvent(MakeEvent(sessionId, "message.part.updated",
                new { sessionID = sessionId, part = new { id = partId, sessionID = sessionId, messageID = messageId, type = "text", text } }),
                responseDelay)
            .AddEvent(MakeEvent(sessionId, "session.idle",
                new { sessionId, status = new { type = "idle" } }),
                responseDelay)
        );
    }

    // ── Error scenarios ──────────────────────────────────────────────────────

    /// <summary>Configure the harness to throw when <c>SpawnAsync</c> is called.</summary>
    public TestScenarioBuilder WithSpawnFailure()
    {
        _throwOnSpawn = true;
        return this;
    }

    /// <summary>Configure the harness to throw when <c>SendPromptAsync</c> is called.</summary>
    public TestScenarioBuilder WithSendPromptFailure()
    {
        _throwOnSendPrompt = true;
        return this;
    }

    // ── Status ───────────────────────────────────────────────────────────────

    /// <summary>Set the initial lifecycle status of the spawned instance.</summary>
    public TestScenarioBuilder WithInitialStatus(HarnessSessionStatus status)
    {
        _initialStatus = status;
        return this;
    }

    // ── Build ────────────────────────────────────────────────────────────────

    public TestScenario Build()
    {
        return new TestScenario
        {
            Messages = _messages.ToList(),
            PromptResponses = _promptResponses,
            ThrowOnSpawn = _throwOnSpawn,
            ThrowOnSendPrompt = _throwOnSendPrompt,
            InitialStatus = _initialStatus
        };
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static HarnessEvent MakeEvent(string sessionId, string type, object payload)
    {
        return new HarnessEvent
        {
            Type = type,
            SessionId = sessionId,
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(payload)
        };
    }
}

/// <summary>Fluent builder for a single prompt response sequence.</summary>
public sealed class PromptResponseBuilder
{
    private readonly List<ScenarioEvent> _events = [];

    public PromptResponseBuilder AddEvent(HarnessEvent evt, TimeSpan? delay = null)
    {
        _events.Add(new ScenarioEvent
        {
            Event = evt,
            Delay = delay ?? TimeSpan.Zero
        });
        return this;
    }

    internal IReadOnlyList<ScenarioEvent> Build() => _events.ToList();
}


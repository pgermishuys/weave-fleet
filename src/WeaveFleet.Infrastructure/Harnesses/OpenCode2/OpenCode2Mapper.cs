using System.Text.Json;
using WeaveFleet.Application.Analytics;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode2;

/// <summary>
/// Turns one V2 session's events into Fleet's harness events. V2 streams a turn as
/// <c>session.execution.*</c> (the turn), <c>session.step.*</c> (one model call, one assistant message) and
/// <c>session.text.*</c> / <c>session.reasoning.*</c> (a part of that message, by ordinal). Events Fleet has no
/// use for yet (tools, forms, permissions, inbox, catalog changes) map to nothing, as does anything unknown.
/// </summary>
/// <remarks>One mapper per session, fed from one event stream: not thread-safe.</remarks>
internal sealed class OpenCode2Mapper(string fleetSessionId)
{
    private readonly Dictionary<string, AssistantMessage> _messages = new(StringComparer.Ordinal);
    private int _nextStepIndex;

    /// <summary>The model of the latest step, for analytics.</summary>
    public (string? ProviderId, string? ModelId) CurrentModel { get; private set; }

    public IReadOnlyList<HarnessEvent> Map(OpenCode2Event evt)
    {
        var data = evt.Data;
        if (data.ValueKind != JsonValueKind.Object)
            return [];

        return evt.Type switch
        {
            "session.execution.started" => [Status(ActivityStatuses.Busy)],
            "session.execution.succeeded" or "session.execution.interrupted" => [Idle()],
            "session.execution.failed" => [Error(ReadError(data)), Idle()],
            "session.retry.scheduled" => [Retry(data)],
            "session.step.started" => StepStarted(evt, data),
            "session.step.ended" => StepEnded(evt, data, ReadString(data, "finish")),
            "session.step.failed" => StepEnded(evt, data, finish: null),
            "session.text.started" => PartStarted(data, "text"),
            "session.text.delta" => PartDelta(data, "text"),
            "session.text.ended" => PartEnded(data, "text"),
            "session.reasoning.started" => PartStarted(data, "reasoning"),
            "session.reasoning.delta" => PartDelta(data, "reasoning"),
            "session.reasoning.ended" => PartEnded(data, "reasoning"),
            _ => [],
        };
    }

    /// <summary>
    /// What a finished step used, for Fleet's token analytics: <c>session.step.ended</c> and
    /// <c>session.step.failed</c> carry the step's own tokens and cost. <c>session.usage.updated</c> repeats the
    /// session's running totals, so it isn't counted.
    /// </summary>
    public TokenEventData? TryReadStepUsage(
        OpenCode2Event evt,
        string? projectId,
        string? projectName,
        string? workspaceDirectory,
        string userId)
    {
        if (evt.Type is not ("session.step.ended" or "session.step.failed")
            || evt.Data.ValueKind != JsonValueKind.Object
            || !evt.Data.TryGetProperty("tokens", out var tokens)
            || tokens.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var messageId = ReadString(evt.Data, "assistantMessageID");
        var model = messageId is not null && _messages.TryGetValue(messageId, out var message)
            ? (message.ProviderId, message.ModelId)
            : CurrentModel;

        var input = ReadDouble(tokens, "input") ?? 0;
        var output = ReadDouble(tokens, "output") ?? 0;
        var reasoning = ReadDouble(tokens, "reasoning") ?? 0;
        double cacheRead = 0, cacheWrite = 0;
        if (tokens.TryGetProperty("cache", out var cache) && cache.ValueKind == JsonValueKind.Object)
        {
            cacheRead = ReadDouble(cache, "read") ?? 0;
            cacheWrite = ReadDouble(cache, "write") ?? 0;
        }

        return new TokenEventData(
            EventId: evt.Id ?? $"{fleetSessionId}:{messageId}",
            SessionId: fleetSessionId,
            ProjectId: projectId,
            ProjectName: projectName,
            WorkspaceDirectory: workspaceDirectory,
            ModelId: model.ModelId,
            ProviderId: model.ProviderId,
            TokensInput: input,
            TokensOutput: output,
            TokensReasoning: reasoning,
            TokensCacheRead: cacheRead,
            TokensCacheWrite: cacheWrite,
            TokensTotal: input + output + reasoning,
            Cost: ReadDouble(evt.Data, "cost") ?? 0,
            EstimatedCost: ModelPricing.EstimateCost(model.ModelId, input, output, reasoning, cacheRead),
            CreatedAt: evt.Created is { } created ? DateTimeOffset.FromUnixTimeMilliseconds(created) : DateTimeOffset.UtcNow,
            UserId: userId);
    }

    /// <summary>The id Fleet gives part <paramref name="ordinal"/> of kind <paramref name="kind"/> in a V2 message.</summary>
    internal static string PartId(string messageId, string kind, int ordinal) => $"{messageId}-{kind}-{ordinal}";

    private List<HarnessEvent> StepStarted(OpenCode2Event evt, JsonElement data)
    {
        if (ReadString(data, "assistantMessageID") is not { } messageId)
            return [];

        string? providerId = null, modelId = null;
        if (data.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.Object)
        {
            providerId = ReadString(model, "providerID");
            modelId = ReadString(model, "id");
        }

        var message = new AssistantMessage(
            evt.Created ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ReadString(data, "agent"),
            providerId,
            modelId,
            _nextStepIndex++);
        _messages[messageId] = message;
        CurrentModel = (providerId, modelId);

        return
        [
            MessageUpdated(messageId, message, completed: null, cost: null, tokens: null, finish: null),
            PartUpdated(new OpenCode2Part
            {
                Id = $"{messageId}-step-start",
                SessionId = fleetSessionId,
                MessageId = messageId,
                Type = "step-start",
                Index = message.StepIndex,
            }),
        ];
    }

    private List<HarnessEvent> StepEnded(OpenCode2Event evt, JsonElement data, string? finish)
    {
        if (ReadString(data, "assistantMessageID") is not { } messageId)
            return [];

        var message = _messages.GetValueOrDefault(messageId)
            ?? new AssistantMessage(evt.Created ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), null, null, null, _nextStepIndex++);
        var completed = evt.Created ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var cost = ReadDouble(data, "cost");
        var tokens = data.TryGetProperty("tokens", out var t) && t.ValueKind == JsonValueKind.Object
            ? new OpenCode2Tokens
            {
                Input = ReadDouble(t, "input") ?? 0,
                Output = ReadDouble(t, "output") ?? 0,
                Reasoning = ReadDouble(t, "reasoning") ?? 0,
            }
            : null;
        _messages.Remove(messageId);

        return
        [
            MessageUpdated(messageId, message, completed, cost, tokens, finish),
            PartUpdated(new OpenCode2Part
            {
                Id = $"{messageId}-step-finish",
                SessionId = fleetSessionId,
                MessageId = messageId,
                Type = "step-finish",
                Index = message.StepIndex,
                Reason = finish,
                Cost = cost ?? 0,
                Tokens = tokens,
                CompletedAt = completed,
            }),
        ];
    }

    /// <summary>A new part starts empty, so the deltas that follow have somewhere to go.</summary>
    private List<HarnessEvent> PartStarted(JsonElement data, string kind)
        => ReadPartRef(data, kind) is { } part
            ? [PartUpdated(TextPart(part, kind, string.Empty))]
            : [];

    private List<HarnessEvent> PartDelta(JsonElement data, string kind)
    {
        if (ReadPartRef(data, kind) is not { } part || ReadString(data, "delta") is not { Length: > 0 } delta)
            return [];

        return
        [
            Event(EventTypes.MessagePartDelta, JsonSerializer.SerializeToElement(
                new OpenCode2PartDeltaPayload
                {
                    SessionId = fleetSessionId,
                    MessageId = part.MessageId,
                    PartId = part.PartId,
                    Field = "text",
                    Delta = delta,
                },
                OpenCode2JsonContext.Default.OpenCode2PartDeltaPayload)),
        ];
    }

    /// <summary>The end of a part carries its whole text, which replaces what the deltas built up.</summary>
    private List<HarnessEvent> PartEnded(JsonElement data, string kind)
        => ReadPartRef(data, kind) is { } part
            ? [PartUpdated(TextPart(part, kind, ReadString(data, "text") ?? string.Empty))]
            : [];

    private OpenCode2Part TextPart((string MessageId, string PartId) part, string kind, string text) => new()
    {
        Id = part.PartId,
        SessionId = fleetSessionId,
        MessageId = part.MessageId,
        Type = kind,
        Text = text,
    };

    private static (string MessageId, string PartId)? ReadPartRef(JsonElement data, string kind)
    {
        if (ReadString(data, "assistantMessageID") is not { } messageId)
            return null;

        var ordinal = data.TryGetProperty("ordinal", out var o) && o.TryGetInt32(out var value) ? value : 0;
        return (messageId, PartId(messageId, kind, ordinal));
    }

    private HarnessEvent MessageUpdated(
        string messageId,
        AssistantMessage message,
        long? completed,
        double? cost,
        OpenCode2Tokens? tokens,
        string? finish)
        => Event(EventTypes.MessageUpdated, JsonSerializer.SerializeToElement(
            new OpenCode2MessageUpdatedPayload
            {
                Info = new OpenCode2MessageInfo
                {
                    Id = messageId,
                    Role = "assistant",
                    SessionId = fleetSessionId,
                    Agent = message.Agent,
                    ModelId = message.ModelId,
                    ProviderId = message.ProviderId,
                    Time = new OpenCode2MessageTime { Created = message.Created, Completed = completed },
                    Cost = cost,
                    Tokens = tokens,
                    Finish = finish,
                },
            },
            OpenCode2JsonContext.Default.OpenCode2MessageUpdatedPayload));

    private HarnessEvent PartUpdated(OpenCode2Part part)
        => Event(EventTypes.MessagePartUpdated, JsonSerializer.SerializeToElement(
            new OpenCode2PartUpdatedPayload { SessionId = fleetSessionId, Part = part },
            OpenCode2JsonContext.Default.OpenCode2PartUpdatedPayload));

    internal HarnessEvent Status(string type)
        => Event(EventTypes.SessionStatus, JsonSerializer.SerializeToElement(
            new OpenCode2StatusPayload { SessionId = fleetSessionId, Status = new OpenCode2Status { Type = type } },
            OpenCode2JsonContext.Default.OpenCode2StatusPayload));

    internal HarnessEvent Idle()
        => Event(EventTypes.SessionIdle, JsonSerializer.SerializeToElement(
            new OpenCode2SessionPayload { SessionId = fleetSessionId },
            OpenCode2JsonContext.Default.OpenCode2SessionPayload));

    internal HarnessEvent Error(OpenCode2ErrorInfo error)
        => Event(EventTypes.SessionError, JsonSerializer.SerializeToElement(
            new OpenCode2ErrorPayload { SessionId = fleetSessionId, Error = error },
            OpenCode2JsonContext.Default.OpenCode2ErrorPayload));

    /// <summary>V2 says when it will try again (<c>at</c>, epoch milliseconds); Fleet's retry status wants how long until then.</summary>
    private HarnessEvent Retry(JsonElement data)
    {
        long? delay = data.TryGetProperty("at", out var at) && at.TryGetInt64(out var atMs)
            ? Math.Max(0, atMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            : null;

        return Event(EventTypes.SessionStatus, JsonSerializer.SerializeToElement(
            new OpenCode2StatusPayload
            {
                SessionId = fleetSessionId,
                Status = new OpenCode2Status
                {
                    Type = ActivityStatuses.Retry,
                    Count = data.TryGetProperty("attempt", out var attempt) && attempt.TryGetInt32(out var n) ? n : null,
                    Reason = ReadError(data).Message,
                    Delay = delay,
                },
            },
            OpenCode2JsonContext.Default.OpenCode2StatusPayload));
    }

    /// <summary>V2's <c>Session.StructuredError</c>: <c>{ type, message, status? }</c>.</summary>
    private static OpenCode2ErrorInfo ReadError(JsonElement data)
    {
        var error = data.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object ? e : default;
        var type = error.ValueKind == JsonValueKind.Object ? ReadString(error, "type") : null;
        var message = error.ValueKind == JsonValueKind.Object ? ReadString(error, "message") : null;
        return new OpenCode2ErrorInfo
        {
            Name = type ?? "Error",
            Message = message ?? type ?? "OpenCode 2 stopped the turn without saying why.",
        };
    }

    private HarnessEvent Event(string type, JsonElement payload) => new()
    {
        Type = type,
        SessionId = fleetSessionId,
        Timestamp = DateTimeOffset.UtcNow,
        Payload = payload,
    };

    private static string? ReadString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? ReadDouble(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    /// <summary>What a step's <c>session.step.started</c> said, kept until the step ends.</summary>
    private sealed record AssistantMessage(long Created, string? Agent, string? ProviderId, string? ModelId, int StepIndex);
}

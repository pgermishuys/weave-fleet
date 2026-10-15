using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses.Acp;

/// <summary>
/// Turns an ACP agent's <c>session/update</c>s into Fleet's harness events, and keeps the conversation they build so the
/// session can show it again (<see cref="Messages"/>), including after <c>session/load</c> replays it. A turn is one
/// assistant message: text and thinking grow in place, and each tool call is a part that moves from running to done.
/// The agent's <c>_fleet/fusion</c> notifications (HydraFusion passes) become tool rows named <see cref="PassTool"/>.
/// </summary>
internal sealed class AcpMapper(string sessionId)
{
    /// <summary>The tool name a HydraFusion pass is shown under; the client draws it as a row of its own.</summary>
    internal const string PassTool = "hydrafusion_pass";

    private readonly List<HarnessMessage> _history = [];
    private readonly List<MessagePart> _parts = [];
    private readonly Dictionary<string, int> _tools = [];
    private readonly StringBuilder _open = new();
    private string? _openKind;
    private string? _openPartId;
    private string? _assistantId;
    private DateTimeOffset _assistantAt;
    private bool _userOpen;
    private int _messageCount;
    private int _partCount;

    /// <summary>The conversation so far, oldest first, with the turn still running as its last message.</summary>
    internal IReadOnlyList<HarnessMessage> Messages() => _assistantId is null ? _history : [.. _history, Assistant()];

    /// <summary>The user's prompt, which starts a turn.</summary>
    internal void UserPrompt(string text)
    {
        CommitAssistant();
        AddUser(text);
        _userOpen = false;
    }

    /// <summary>Maps one <c>session/update</c> body (<c>params.update</c>).</summary>
    internal IReadOnlyList<HarnessEvent> Update(JsonElement update)
    {
        var events = new List<HarnessEvent>();
        switch (Str(update, "sessionUpdate"))
        {
            case "user_message_chunk":
                if (!_userOpen)
                {
                    CommitAssistant();
                    AddUser("");
                    _userOpen = true;
                }

                AppendToUser(ChunkText(update));
                break;
            case "agent_message_chunk":
                Chunk(events, "text", ChunkText(update));
                break;
            case "agent_thought_chunk":
                Chunk(events, "reasoning", ChunkText(update));
                break;
            case "tool_call":
            case "tool_call_update":
                Tool(events, update);
                break;
        }

        return events;
    }

    /// <summary>Maps a <c>_fleet/fusion</c> notification: a pass started, or finished with its time and credits.</summary>
    internal IReadOnlyList<HarnessEvent> Fusion(JsonElement p)
    {
        var kind = Str(p, "event");
        if (kind is not ("phase_started" or "phase_completed"))
            return [];

        var events = new List<HarnessEvent>();
        var done = kind == "phase_completed";
        var input = new JsonObject
        {
            ["pattern"] = Str(p, "pattern"),
            ["role"] = Str(p, "role") ?? Str(p, "phase"),
            ["model"] = Str(p, "model"),
        };
        if (done && p.TryGetProperty("durationMs", out var ms) && ms.TryGetInt64(out var millis))
            input["durationMs"] = millis;
        if (done && p.TryGetProperty("credits", out var credits) && credits.TryGetDouble(out var spent))
            input["credits"] = Math.Round(spent, 2);
        if (Str(p, "verdict") is { } verdict)
            input["verdict"] = verdict;

        var index = p.TryGetProperty("index", out var i) && i.TryGetInt32(out var n) ? n : _tools.Count;
        var failed = done && Str(p, "status") is { } status && status is not ("succeeded" or "completed" or "skipped");
        ToolInvocationState state = !done ? new ToolRunningState { Input = Json(input) }
            : failed ? new ToolErrorState { Input = Json(input), Error = Str(p, "status") }
            : new ToolCompletedState { Input = Json(input), Title = Str(p, "role") };
        UpsertTool(events, $"fusion-{index}", PassTool, input, state, done ? (failed ? ToolUseState.Error : ToolUseState.Completed) : ToolUseState.Running);
        return events;
    }

    /// <summary>The turn is over: its message is complete, and the session is idle.</summary>
    internal IReadOnlyList<HarnessEvent> EndTurn(string? stopReason)
    {
        var events = new List<HarnessEvent>();
        SettleTools(events, stopReason == "cancelled" ? "Stopped before it finished." : "The turn ended before this call did.");
        if (_assistantId is { } id)
        {
            events.Add(Event(EventTypes.MessageUpdated, Lifecycle(id, completed: true)));
            CommitAssistant();
        }

        events.Add(Status(ActivityStatuses.Idle));
        return events;
    }

    /// <summary>The turn failed, in words the user can read: a failure card, then idle.</summary>
    internal IReadOnlyList<HarnessEvent> Fail(string message)
    {
        var events = new List<HarnessEvent>();
        SettleTools(events, message);
        events.Add(Event(EventTypes.SessionError, JsonSerializer.SerializeToElement(
            new TurnFailedPayload { SessionId = sessionId, MessageId = _assistantId, Error = new TurnError { Name = "AcpError", Message = message } },
            InfrastructureJsonContext.Default.TurnFailedPayload)));
        if (_assistantId is { } id)
        {
            events.Add(Event(EventTypes.MessageUpdated, Lifecycle(id, completed: true)));
            CommitAssistant();
        }

        events.Add(Status(ActivityStatuses.Idle));
        return events;
    }

    internal HarnessEvent Status(string status) => Event(EventTypes.SessionStatus, JsonSerializer.SerializeToElement(
        new SessionStatusEventPayload { Status = new SessionStatusEventKind { Type = status } },
        InfrastructureJsonContext.Default.SessionStatusEventPayload));

    private void Chunk(List<HarnessEvent> events, string kind, string text)
    {
        _userOpen = false;
        if (text.Length == 0)
            return;

        EnsureAssistant(events);
        if (_openKind == kind && _openPartId is { } open)
        {
            _open.Append(text);
            _parts[PartIndex(open)] = Part(kind, _open.ToString(), open);
            events.Add(Event(EventTypes.MessagePartDelta, JsonSerializer.SerializeToElement(
                new MessagePartDeltaStreamedPayload { SessionId = sessionId, MessageId = _assistantId!, PartId = open, Field = "text", Delta = text },
                InfrastructureJsonContext.Default.MessagePartDeltaStreamedPayload)));
            return;
        }

        _openKind = kind;
        _openPartId = NextPartId();
        _open.Clear().Append(text);
        _parts.Add(Part(kind, text, _openPartId));
        events.Add(PartEvent(kind == "text"
            ? new TextMessageEventPart { Id = _openPartId, SessionId = sessionId, MessageId = _assistantId!, Text = text }
            : new ReasoningMessageEventPart { Id = _openPartId, SessionId = sessionId, MessageId = _assistantId!, Text = text }));
    }

    private void Tool(List<HarnessEvent> events, JsonElement update)
    {
        _userOpen = false;
        var callId = Str(update, "toolCallId");
        if (callId is null)
            return;

        var prior = _tools.TryGetValue(callId, out var at) ? _parts[at] as ToolUsePart : null;
        var kind = Str(update, "kind");
        var name = kind is not null ? ToolName(kind) : prior?.ToolName ?? "tool";
        var input = prior?.Arguments ?? default;
        var node = update.TryGetProperty("rawInput", out var raw) && raw.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(raw.GetRawText())!.AsObject()
            : input.ValueKind == JsonValueKind.Object ? JsonNode.Parse(input.GetRawText())!.AsObject() : new JsonObject();
        if (node["path"] is null && node["filePath"] is null && FirstPath(update) is { } path)
            node["path"] = path;
        if (node["description"] is null && Str(update, "title") is { } title)
            node["description"] = title;

        var status = Str(update, "status") ?? (prior is not null ? "in_progress" : "pending");
        var output = ToolOutput(update);
        ToolInvocationState state = status switch
        {
            "completed" => new ToolCompletedState { Input = Json(node), Output = output is null ? null : Json(output), Title = Str(update, "title") },
            "failed" => new ToolErrorState { Input = Json(node), Output = output is null ? null : Json(output), Error = output ?? "The tool call failed." },
            "pending" => new ToolPendingState { Input = Json(node) },
            _ => new ToolRunningState { Input = Json(node) },
        };
        var useState = status switch { "completed" => ToolUseState.Completed, "failed" => ToolUseState.Error, "pending" => ToolUseState.Pending, _ => ToolUseState.Running };
        UpsertTool(events, callId, name, node, state, useState, output);

        if (status == "completed" && name == "edit" && (FirstPath(update) ?? node["path"]?.GetValue<string>()) is { } written)
        {
            events.Add(Event(EventTypes.FilesWritten, JsonSerializer.SerializeToElement(
                new FilesWrittenPayload { SessionId = sessionId, MessageId = _assistantId, Paths = [written] },
                InfrastructureJsonContext.Default.FilesWrittenPayload)));
        }
    }

    private void UpsertTool(List<HarnessEvent> events, string callId, string name, JsonNode input, ToolInvocationState state, ToolUseState useState, string? output = null)
    {
        EnsureAssistant(events);
        _openKind = null;
        _openPartId = null;
        var existing = _tools.TryGetValue(callId, out var index);
        var partId = existing && _parts[index] is ToolUsePart { PartId: { } id } ? id : NextPartId();
        var part = new ToolUsePart(callId, name, Json(input), useState)
        {
            PartId = partId,
            Output = output is null ? null : Json(output),
            Error = useState == ToolUseState.Error ? output : null,
        };
        if (existing)
        {
            _parts[index] = part;
        }
        else
        {
            _tools[callId] = _parts.Count;
            _parts.Add(part);
        }

        events.Add(PartEvent(new ToolMessageEventPart { Id = partId, SessionId = sessionId, MessageId = _assistantId!, ToolName = name, CallId = callId, State = state }));
    }

    /// <summary>A call the turn left open (stopped, or failed) would read as running forever.</summary>
    private void SettleTools(List<HarnessEvent> events, string reason)
    {
        foreach (var (callId, index) in _tools.ToList())
        {
            if (_parts[index] is not ToolUsePart { State: ToolUseState.Pending or ToolUseState.Running } tool)
                continue;
            _parts[index] = tool with { State = ToolUseState.Error, Error = reason };
            events.Add(PartEvent(new ToolMessageEventPart
            {
                Id = tool.PartId!, SessionId = sessionId, MessageId = _assistantId!, ToolName = tool.ToolName, CallId = callId,
                State = new ToolErrorState { Input = tool.Arguments, Error = reason },
            }));
        }
    }

    private void EnsureAssistant(List<HarnessEvent> events)
    {
        if (_assistantId is not null)
            return;
        _assistantId = $"acp-{sessionId}-{++_messageCount}";
        _assistantAt = DateTimeOffset.UtcNow;
        events.Add(Event(EventTypes.MessageCreated, Lifecycle(_assistantId, completed: false)));
    }

    private void CommitAssistant()
    {
        if (_assistantId is null)
            return;
        if (_parts.Count > 0)
            _history.Add(Assistant());
        _assistantId = null;
        _parts.Clear();
        _tools.Clear();
        _open.Clear();
        _openKind = _openPartId = null;
    }

    private HarnessMessage Assistant() => new() { Id = _assistantId!, Role = "assistant", Parts = [.. _parts], Timestamp = _assistantAt };

    private void AddUser(string text)
        => _history.Add(new HarnessMessage { Id = $"acp-{sessionId}-{++_messageCount}", Role = "user", Parts = [new TextPart(text)], Timestamp = DateTimeOffset.UtcNow });

    private void AppendToUser(string text)
    {
        var last = _history[^1];
        _history[^1] = last with { Parts = [new TextPart(last.TextContent + text)] };
    }

    private JsonElement Lifecycle(string messageId, bool completed) => JsonSerializer.SerializeToElement(
        new MessageLifecyclePayload
        {
            Info = new MessageEventInfo
            {
                Id = messageId,
                Role = "assistant",
                SessionId = sessionId,
                Time = new MessageEventTime
                {
                    Created = _assistantAt.ToUnixTimeMilliseconds(),
                    Completed = completed ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : null,
                },
            },
        },
        InfrastructureJsonContext.Default.MessageLifecyclePayload);

    private HarnessEvent PartEvent(MessageEventPart part) => Event(EventTypes.MessagePartUpdated, JsonSerializer.SerializeToElement(
        new MessagePartUpdatedPayload { SessionId = sessionId, Part = part }, InfrastructureJsonContext.Default.MessagePartUpdatedPayload));

    private HarnessEvent Event(string type, JsonElement payload) => new()
    {
        Type = type, SessionId = sessionId, Timestamp = DateTimeOffset.UtcNow, Payload = payload,
    };

    private string NextPartId() => $"{_assistantId}-p{++_partCount}";

    private int PartIndex(string partId) => _parts.FindIndex(p => p is TextPart { PartId: var t } && t == partId || p is ReasoningPart { PartId: var r } && r == partId);

    private static MessagePart Part(string kind, string text, string partId)
        => kind == "text" ? new TextPart(text) { PartId = partId } : new ReasoningPart(text) { PartId = partId };

    /// <summary>ACP's tool kinds under the names Fleet already treats (and asks about) the same way.</summary>
    internal static string ToolName(string? kind) => kind switch
    {
        "read" => "read",
        "edit" => "edit",
        "delete" => "edit",
        "move" => "move",
        "search" => "grep",
        "execute" => "bash",
        "fetch" => "webfetch",
        "think" => "task",
        _ => "tool",
    };

    private static string? FirstPath(JsonElement update)
        => update.TryGetProperty("locations", out var locations) && locations.ValueKind == JsonValueKind.Array && locations.GetArrayLength() > 0
            ? Str(locations[0], "path")
            : null;

    /// <summary>What the call returned: its text, or a note about the diff it made, else its raw output.</summary>
    private static string? ToolOutput(JsonElement update)
    {
        if (update.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            var text = new StringBuilder();
            foreach (var item in content.EnumerateArray())
            {
                if (Str(item, "type") == "diff")
                    text.Append("Edited ").Append(Str(item, "path")).Append('\n');
                else if (item.TryGetProperty("content", out var inner) && Str(inner, "text") is { } t)
                    text.Append(t).Append('\n');
            }

            if (text.Length > 0)
                return text.ToString().TrimEnd();
        }

        return update.TryGetProperty("rawOutput", out var raw) && raw.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? raw.ValueKind == JsonValueKind.String ? raw.GetString() : Str(raw, "message") ?? raw.GetRawText()
            : null;
    }

    private static string ChunkText(JsonElement update)
        => update.TryGetProperty("content", out var content) && Str(content, "text") is { } text ? text : "";

    internal static string? Str(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static JsonElement Json(JsonNode node) => JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();

    private static JsonElement Json(string text) => JsonDocument.Parse(JsonValue.Create(text).ToJsonString()).RootElement.Clone();

    internal static string Seconds(long millis) => (millis / 1000.0).ToString("0.0", CultureInfo.InvariantCulture);
}

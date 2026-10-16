using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WeaveFleet.Application.FleetTools;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Identity;

namespace WeaveFleet.Infrastructure.Harnesses.Acp;

/// <summary>
/// Turns an ACP agent's <c>session/update</c>s into Fleet's harness events, and keeps the conversation they build so the
/// session can show it again (<see cref="Messages"/>), including after <c>session/load</c> replays it. A turn is one
/// assistant message per step, as the other harnesses send them: text and thinking grow in place, each tool call is a part
/// that moves from running to done, and text or thinking after a call starts the next message (Fleet draws a message's
/// text above its calls, so a reply kept in the message of the calls it follows would show above them).
/// The agent's <c>_fleet/fusion</c> notifications (HydraFusion passes) become tool rows named <see cref="PassTool"/>, and a
/// turn's workflow and credits one row named <see cref="TurnTool"/> at its head. What a turn used comes as a step-finish
/// part (<see cref="Usage"/>), which is where Fleet reads the session's tokens from. Messages have ascending ids, as the
/// other harnesses' do, and a prompt keeps the id Fleet gave it: the client places a message by its id.
/// </summary>
/// <param name="fleetToolCall">
/// What Fleet's MCP server kept about one of its own tool calls (the card's title, and the canvas, screenshot or page it
/// made), by the tool's name and the arguments the agent called it with; null when it kept nothing.
/// </param>
internal sealed class AcpMapper(string sessionId, Func<string, JsonElement, FleetToolCallRecord?>? fleetToolCall = null)
{
    /// <summary>The tool name a HydraFusion pass is shown under; the client draws it as a row of its own.</summary>
    internal const string PassTool = "hydrafusion_pass";

    /// <summary>The tool name of a turn's summary row: the workflow it ran (Single, Cascade, Critique) and the credits it cost.</summary>
    internal const string TurnTool = "copilot_turn";

    // A steer comes in on the request thread while the read loop is mapping updates.
    private readonly Lock _lock = new();
    private readonly List<HarnessMessage> _history = [];
    private readonly List<MessagePart> _parts = [];
    private readonly Dictionary<string, int> _tools = [];
    // The arguments each call to one of Fleet's tools was sent with, before Fleet adds a path or a description for the row.
    private readonly Dictionary<string, JsonElement> _arguments = [];
    private readonly HashSet<string> _closedCalls = [];
    // Calls a steer left running in the message it closed: message id and where that message sits in the history.
    private readonly Dictionary<string, (string MessageId, int HistoryIndex)> _detached = [];
    private IReadOnlyList<TodoEntry> _todos = [];
    private readonly StringBuilder _open = new();
    private string? _openKind;
    private string? _openPartId;
    private string? _assistantId;
    private DateTimeOffset _assistantAt;
    private bool _userOpen;
    private int _partCount;
    private int _stepCount;
    private bool _turnRow;

    /// <summary>The agent's todo list as last reported (<c>plan</c> updates), including while a loaded conversation replays.</summary>
    internal IReadOnlyList<TodoEntry> Todos() { lock (_lock) return _todos; }

    /// <summary>The conversation so far, oldest first, with the turn still running as its last message.</summary>
    internal IReadOnlyList<HarnessMessage> Messages() { lock (_lock) return [.. MessagesCore()]; }

    /// <summary>
    /// The user's prompt, which starts a turn, under the id Fleet gave it (<paramref name="messageId"/>). Mid-turn (a
    /// steer) it closes the message in progress: that message is completed and its calls still running are settled, so
    /// none of them reads as running forever.
    /// </summary>
    internal IReadOnlyList<HarnessEvent> UserPrompt(string text, IReadOnlyList<HarnessAttachment>? images = null, string? messageId = null)
    {
        lock (_lock) return UserPromptCore(text, images, messageId);
    }

    /// <summary>Maps one <c>session/update</c> body (<c>params.update</c>).</summary>
    internal IReadOnlyList<HarnessEvent> Update(JsonElement update) { lock (_lock) return UpdateCore(update); }

    /// <summary>Maps a <c>_fleet/fusion</c> notification: a pass started, or finished with its time and credits.</summary>
    internal IReadOnlyList<HarnessEvent> Fusion(JsonElement p) { lock (_lock) return FusionCore(p); }

    /// <summary>What the finished turn used (the prompt result's <c>usage</c>): its tokens, and credits when Copilot reported them.</summary>
    internal IReadOnlyList<HarnessEvent> Usage(JsonElement usage) { lock (_lock) return UsageCore(usage); }

    /// <summary>The turn is over: its message is complete, and the session is idle.</summary>
    internal IReadOnlyList<HarnessEvent> EndTurn(string? stopReason) { lock (_lock) return EndTurnCore(stopReason); }

    /// <summary>The turn failed, in words the user can read: a failure card, then idle.</summary>
    internal IReadOnlyList<HarnessEvent> Fail(string message) { lock (_lock) return FailCore(message); }

    /// <summary>The conversation so far, oldest first, with the turn still running as its last message.</summary>
    private List<HarnessMessage> MessagesCore() => _assistantId is null ? _history : [.. _history, Assistant()];

    private List<HarnessEvent> UserPromptCore(string text, IReadOnlyList<HarnessAttachment>? images, string? messageId)
    {
        var events = new List<HarnessEvent>();
        // A call still running goes on running after a steer (Copilot moves a shell command to the background).
        CloseStep(events);
        _turnRow = false;
        AddUser(text, images, messageId);
        _userOpen = false;
        return events;
    }

    /// <summary>
    /// Completes the message in progress and keeps it in the history. A call still running in it stays as it is and takes
    /// the result the agent reports later, in that message; the rest are finished.
    /// </summary>
    private void CloseStep(List<HarnessEvent> events)
    {
        if (_assistantId is not { } id)
            return;
        var historyIndex = _history.Count;
        foreach (var part in _parts.OfType<ToolUsePart>())
        {
            if (part.State is ToolUseState.Pending or ToolUseState.Running)
                _detached[part.ToolCallId] = (id, historyIndex);
            else
                _closedCalls.Add(part.ToolCallId);
        }

        events.Add(Event(EventTypes.MessageUpdated, Lifecycle(id, completed: true)));
        CommitAssistant();
    }

    /// <summary>Maps one <c>session/update</c> body (<c>params.update</c>).</summary>
    private List<HarnessEvent> UpdateCore(JsonElement update)
    {
        var events = new List<HarnessEvent>();
        switch (Str(update, "sessionUpdate"))
        {
            case "user_message_chunk":
                if (!_userOpen)
                {
                    CommitAssistant();
                    _turnRow = false;
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
            case "plan":
                Plan(events, update);
                break;
        }

        return events;
    }

    /// <summary>
    /// Maps a <c>_fleet/fusion</c> notification: the route chosen or the turn finished (the turn row), or a pass started or
    /// finished with its time, credits and verdict (a pass row).
    /// </summary>
    private List<HarnessEvent> FusionCore(JsonElement p)
    {
        var kind = Str(p, "event");
        var events = new List<HarnessEvent>();
        if (kind is "resolved" or "completed")
        {
            TurnRow(events, p, finished: kind == "completed");
            return events;
        }

        if (kind is not ("phase_started" or "phase_completed"))
            return [];

        var done = kind == "phase_completed";
        var input = new JsonObject
        {
            ["pattern"] = Str(p, "pattern"),
            ["role"] = Str(p, "role") ?? Str(p, "phase"),
            ["model"] = Str(p, "model"),
        };
        if (done && Number(p, "durationMs") is { } millis)
            input["durationMs"] = (long)millis;
        if (done && Number(p, "credits") is { } spent)
            input["credits"] = Math.Round(spent, 2);
        if (Str(p, "verdict") is { } verdict)
            input["verdict"] = verdict;
        if (Str(p, "reason") is { } reason)
            input["reason"] = reason;

        var index = Number(p, "index") is { } n ? (int)n : _tools.Count;
        var failed = done && Str(p, "status") is { } status && status is not ("succeeded" or "completed" or "skipped");
        ToolInvocationState state = !done ? new ToolRunningState { Input = Json(input) }
            : failed ? new ToolErrorState { Input = Json(input), Error = Str(p, "reason") ?? Str(p, "status") }
            : new ToolCompletedState { Input = Json(input), Title = Str(p, "role") };
        UpsertTool(events, $"fusion-{index}", PassTool, input, state, done ? (failed ? ToolUseState.Error : ToolUseState.Completed) : ToolUseState.Running,
            failed ? Str(p, "reason") ?? Str(p, "status") : null);
        return events;
    }

    /// <summary>The turn's row: running once the route is chosen (workflow and models), finished with its credits and requests.</summary>
    private void TurnRow(List<HarnessEvent> events, JsonElement p, bool finished)
    {
        var input = new JsonObject { ["pattern"] = Str(p, "pattern"), ["model"] = Str(p, "model") };
        if (Str(p, "hint") is { } hint)
            input["hint"] = hint;
        if (finished)
        {
            if (Number(p, "totalCredits") is { } credits)
                input["credits"] = Math.Round(credits, 2);
            if (Number(p, "requests") is { } requests)
                input["requests"] = (long)requests;
            if (Number(p, "durationMs") is { } millis)
                input["durationMs"] = (long)millis;
            if (Str(p, "degradedReason") is { } degraded)
                input["degraded"] = degraded;
        }

        var failed = finished && Str(p, "status") is "failed" or "error";
        ToolInvocationState state = !finished ? new ToolRunningState { Input = Json(input) }
            : failed ? new ToolErrorState { Input = Json(input), Error = Str(p, "status") }
            : new ToolCompletedState { Input = Json(input), Title = Str(p, "pattern") };
        _turnRow = true;
        UpsertTool(events, "fusion-turn", TurnTool, input, state, !finished ? ToolUseState.Running : failed ? ToolUseState.Error : ToolUseState.Completed);
    }

    /// <summary>
    /// A step-finish part with the turn's tokens, which the session's totals and the status bar add up. A turn that cost
    /// credits and has no HydraFusion row yet (a plain model) gets the same row, with only its credits.
    /// </summary>
    private List<HarnessEvent> UsageCore(JsonElement usage)
    {
        var events = new List<HarnessEvent>();
        var input = Number(usage, "inputTokens") ?? 0;
        var thought = Number(usage, "thoughtTokens") ?? 0;
        var output = Number(usage, "outputTokens") ?? 0;
        if (input + output <= 0)
            return events;

        if (!_turnRow && Number(usage, "credits") is > 0 and var credits)
            TurnRow(events, Json(new JsonObject { ["totalCredits"] = credits }), finished: true);

        EnsureAssistant(events);
        var index = _stepCount++;
        var part = new StepFinishPart(index, "stop", 0, input, Math.Max(0, output - thought), thought, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _parts.Add(part);
        events.Add(PartEvent(new StepFinishedMessageEventPart
        {
            Id = $"{_assistantId}-step-finish-{index}", SessionId = sessionId, MessageId = _assistantId!, Index = index, Reason = part.Reason, Cost = 0,
            Tokens = new MessageTokenUsage { Input = part.TokensInput, Output = part.TokensOutput, Reasoning = part.TokensReasoning }, CompletedAt = part.CompletedAt,
        }));
        return events;
    }

    /// <summary>The turn is over: its message is complete, and the session is idle.</summary>
    private List<HarnessEvent> EndTurnCore(string? stopReason)
    {
        var events = new List<HarnessEvent>();
        var ended = stopReason == "cancelled" ? "Stopped before it finished." : "The turn ended before this call did.";
        SettleTools(events, ended);
        SettleDetached(events, ended);
        if (_assistantId is { } id)
        {
            events.Add(Event(EventTypes.MessageUpdated, Lifecycle(id, completed: true)));
            CommitAssistant();
        }

        _closedCalls.Clear();
        _turnRow = false;
        events.Add(Status(ActivityStatuses.Idle));
        return events;
    }

    /// <summary>The turn failed, in words the user can read: a failure card, then idle.</summary>
    private List<HarnessEvent> FailCore(string message)
    {
        var events = new List<HarnessEvent>();
        SettleTools(events, message);
        SettleDetached(events, message);
        events.Add(Event(EventTypes.SessionError, JsonSerializer.SerializeToElement(
            new TurnFailedPayload { SessionId = sessionId, MessageId = _assistantId, Error = new TurnError { Name = "AcpError", Message = message } },
            InfrastructureJsonContext.Default.TurnFailedPayload)));
        if (_assistantId is { } id)
        {
            events.Add(Event(EventTypes.MessageUpdated, Lifecycle(id, completed: true)));
            CommitAssistant();
        }

        _turnRow = false;
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

        if (_parts.Count > 0 && _parts[^1] is ToolUsePart)
            CloseStep(events);
        EnsureAssistant(events);
        if (_openKind == kind && _openPartId is { } open && PartIndex(open) >= 0)
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

    /// <summary>An ACP <c>plan</c> update carries the whole todo list each time; an empty one clears it.</summary>
    private void Plan(List<HarnessEvent> events, JsonElement update)
    {
        List<TodoEntry> items = [];
        if (update.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in entries.EnumerateArray())
            {
                if (Str(entry, "content") is not { Length: > 0 } content)
                    continue;
                items.Add(new TodoEntry { Content = content, Status = TodoStatuses.Normalize(Str(entry, "status")), Priority = Str(entry, "priority") });
            }
        }

        _todos = items;
        events.Add(Event(EventTypes.TodosReported, JsonSerializer.SerializeToElement(
            new TodosReportedPayload { SessionId = sessionId, Items = items }, InfrastructureJsonContext.Default.TodosReportedPayload)));
    }

    private void Tool(List<HarnessEvent> events, JsonElement update)
    {
        _userOpen = false;
        var callId = Str(update, "toolCallId");
        if (callId is null || _closedCalls.Contains(callId))
            return;
        if (_detached.TryGetValue(callId, out var detached))
        {
            LateTool(events, update, callId, detached);
            return;
        }

        var prior = _tools.TryGetValue(callId, out var at) ? _parts[at] as ToolUsePart : null;
        var kind = Str(update, "kind");
        var name = kind is not null ? ToolNameOf(update) : prior?.ToolName ?? "tool";
        var input = prior?.Arguments ?? default;
        var hasRaw = update.TryGetProperty("rawInput", out var raw) && raw.ValueKind == JsonValueKind.Object;
        var fleetTool = FleetToolCatalog.Find(name) is not null;
        if (hasRaw && fleetTool)
            _arguments[callId] = raw.Clone();
        var node = hasRaw
            ? JsonNode.Parse(raw.GetRawText())!.AsObject()
            : input.ValueKind == JsonValueKind.Object ? JsonNode.Parse(input.GetRawText())!.AsObject() : new JsonObject();
        if (node["path"] is null && node["filePath"] is null && FirstPath(update) is { } path)
            node["path"] = path;
        if (node["description"] is null && Str(update, "title") is { } title)
            node["description"] = title;

        var status = Str(update, "status") ?? (prior is not null ? "in_progress" : "pending");
        var output = ToolOutput(update);
        // One of Fleet's own tools: its card's title and what it made, which Fleet's MCP server kept for it, since the
        // result the agent reports carries only what the model reads.
        var fleet = status == "completed" && fleetTool
            ? fleetToolCall?.Invoke(name, _arguments.GetValueOrDefault(callId))
            : null;
        if (status is "completed" or "failed")
            _arguments.Remove(callId);
        ToolInvocationState state = status switch
        {
            "completed" => new ToolCompletedState
            {
                Input = Json(node), Output = output is null ? null : Json(output), Title = fleet?.Title ?? Str(update, "title"), Metadata = fleet?.Metadata,
            },
            "failed" => new ToolErrorState { Input = Json(node), Output = output is null ? null : Json(output), Error = output ?? "The tool call failed." },
            "pending" => new ToolPendingState { Input = Json(node) },
            _ => new ToolRunningState { Input = Json(node) },
        };
        var useState = status switch { "completed" => ToolUseState.Completed, "failed" => ToolUseState.Error, "pending" => ToolUseState.Pending, _ => ToolUseState.Running };
        UpsertTool(events, callId, name, node, state, useState, output, fleet);

        if (status == "completed" && name == "edit" && (FirstPath(update) ?? node["path"]?.GetValue<string>()) is { } written)
        {
            events.Add(Event(EventTypes.FilesWritten, JsonSerializer.SerializeToElement(
                new FilesWrittenPayload { SessionId = sessionId, MessageId = _assistantId, Paths = [written] },
                InfrastructureJsonContext.Default.FilesWrittenPayload)));
            // The Changes panel on an open page refreshes on files.changed (OpenCode's file watcher says it), not on files.written.
            events.Add(Event(EventTypes.FileWatcherUpdated, Json(new JsonObject { ["file"] = written, ["event"] = "change" })));
        }
    }

    private void UpsertTool(
        List<HarnessEvent> events, string callId, string name, JsonNode input, ToolInvocationState state, ToolUseState useState, string? output = null,
        FleetToolCallRecord? fleet = null)
    {
        if (_detached.TryGetValue(callId, out var detached))
        {
            LateRow(events, callId, detached, input, state, useState, output);
            return;
        }

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
            Title = fleet?.Title,
            Metadata = fleet?.Metadata,
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

    /// <summary>What the agent reports about a call a steer left running: the row in the closed message takes the real result.</summary>
    private void LateTool(List<HarnessEvent> events, JsonElement update, string callId, (string MessageId, int HistoryIndex) at)
    {
        var message = _history[at.HistoryIndex];
        var slot = message.Parts.ToList().FindIndex(p => p is ToolUsePart t && t.ToolCallId == callId);
        if (slot < 0 || message.Parts[slot] is not ToolUsePart prior)
            return;

        var status = Str(update, "status") ?? "in_progress";
        var output = ToolOutput(update);
        ToolInvocationState state = status switch
        {
            "completed" => new ToolCompletedState { Input = prior.Arguments, Output = output is null ? null : Json(output), Title = Str(update, "title") },
            "failed" => new ToolErrorState { Input = prior.Arguments, Output = output is null ? null : Json(output), Error = output ?? "The tool call failed." },
            _ => new ToolRunningState { Input = prior.Arguments },
        };
        var useState = status switch { "completed" => ToolUseState.Completed, "failed" => ToolUseState.Error, _ => ToolUseState.Running };
        ReplacePart(message, at.HistoryIndex, slot, prior with { State = useState, Output = output is null ? prior.Output : Json(output), Error = useState == ToolUseState.Error ? output : null });
        events.Add(PartEvent(new ToolMessageEventPart { Id = prior.PartId!, SessionId = sessionId, MessageId = at.MessageId, ToolName = prior.ToolName, CallId = callId, State = state }));
        if (useState != ToolUseState.Running)
        {
            _detached.Remove(callId);
            _closedCalls.Add(callId);
        }
    }

    /// <summary>A HydraFusion row left running in a message that's closed (the answer streams below it) finishes there.</summary>
    private void LateRow(List<HarnessEvent> events, string callId, (string MessageId, int HistoryIndex) at, JsonNode input, ToolInvocationState state, ToolUseState useState, string? output)
    {
        var message = _history[at.HistoryIndex];
        var slot = message.Parts.ToList().FindIndex(p => p is ToolUsePart t && t.ToolCallId == callId);
        if (slot < 0 || message.Parts[slot] is not ToolUsePart prior)
            return;

        ReplacePart(message, at.HistoryIndex, slot, prior with
        {
            Arguments = Json(input), State = useState, Output = output is null ? null : Json(output), Error = useState == ToolUseState.Error ? output : null,
        });
        events.Add(PartEvent(new ToolMessageEventPart { Id = prior.PartId!, SessionId = sessionId, MessageId = at.MessageId, ToolName = prior.ToolName, CallId = callId, State = state }));
        if (useState is not (ToolUseState.Pending or ToolUseState.Running))
            _detached.Remove(callId);
    }

    private void ReplacePart(HarnessMessage message, int historyIndex, int slot, MessagePart part)
    {
        var parts = message.Parts.ToList();
        parts[slot] = part;
        _history[historyIndex] = message with { Parts = parts };
    }

    /// <summary>Calls a steer left running that the turn ends on: they didn't finish, and say so.</summary>
    private void SettleDetached(List<HarnessEvent> events, string reason)
    {
        foreach (var (callId, at) in _detached.ToList())
        {
            var message = _history[at.HistoryIndex];
            var slot = message.Parts.ToList().FindIndex(p => p is ToolUsePart t && t.ToolCallId == callId);
            if (slot < 0 || message.Parts[slot] is not ToolUsePart tool)
                continue;
            ReplacePart(message, at.HistoryIndex, slot, tool with { State = ToolUseState.Error, Error = reason });
            events.Add(PartEvent(new ToolMessageEventPart
            {
                Id = tool.PartId!, SessionId = sessionId, MessageId = at.MessageId, ToolName = tool.ToolName, CallId = callId,
                State = new ToolErrorState { Input = tool.Arguments, Error = reason },
            }));
        }

        _detached.Clear();
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
        _assistantId = AscendingMessageId.New();
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

    /// <summary>The user's message: the text, then each attached image as a file part, as the other harnesses show them.</summary>
    private void AddUser(string text, IReadOnlyList<HarnessAttachment>? images = null, string? messageId = null)
    {
        var id = messageId ?? AscendingMessageId.New();
        List<MessagePart> parts = [new TextPart(text)];
        parts.AddRange((images ?? []).Select((image, index) => new FilePart($"{id}-file-{index}", image.Mime, $"data:{image.Mime};base64,{image.Data}", image.Filename)));
        _history.Add(new HarnessMessage { Id = id, Role = "user", Parts = parts, Timestamp = DateTimeOffset.UtcNow });
    }

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
            // The event replaces the message's parts: without them the finished message would empty itself.
            Parts = EventParts(messageId),
        },
        InfrastructureJsonContext.Default.MessageLifecyclePayload);

    private List<MessageEventPart> EventParts(string messageId)
    {
        var parts = new List<MessageEventPart>(_parts.Count);
        foreach (var part in _parts)
        {
            switch (part)
            {
                case TextPart { PartId: { } id } text:
                    parts.Add(new TextMessageEventPart { Id = id, SessionId = sessionId, MessageId = messageId, Text = text.Text });
                    break;
                case ReasoningPart { PartId: { } id } reasoning:
                    parts.Add(new ReasoningMessageEventPart { Id = id, SessionId = sessionId, MessageId = messageId, Text = reasoning.Text });
                    break;
                case StepFinishPart step:
                    parts.Add(new StepFinishedMessageEventPart
                    {
                        Id = $"{messageId}-step-finish-{step.Index}", SessionId = sessionId, MessageId = messageId, Index = step.Index, Reason = step.Reason, Cost = step.Cost,
                        Tokens = new MessageTokenUsage { Input = step.TokensInput, Output = step.TokensOutput, Reasoning = step.TokensReasoning }, CompletedAt = step.CompletedAt,
                    });
                    break;
                case ToolUsePart { PartId: { } id } tool:
                    ToolInvocationState state = tool.State switch
                    {
                        ToolUseState.Completed => new ToolCompletedState { Input = tool.Arguments, Output = tool.Output, Title = tool.Title, Metadata = tool.Metadata },
                        ToolUseState.Error => new ToolErrorState { Input = tool.Arguments, Error = tool.Error },
                        ToolUseState.Running => new ToolRunningState { Input = tool.Arguments },
                        _ => new ToolPendingState { Input = tool.Arguments },
                    };
                    parts.Add(new ToolMessageEventPart { Id = id, SessionId = sessionId, MessageId = messageId, ToolName = tool.ToolName, CallId = tool.ToolCallId, State = state });
                    break;
            }
        }

        return parts;
    }

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

    /// <summary>
    /// The tool's name for a call or an ask: its ACP kind's name, or, for a kind ACP has no word for ("other": an MCP
    /// tool), the real name an agent gives in <c>_meta.toolName</c> (weave-copilot does), so Fleet's tools read as such.
    /// </summary>
    internal static string ToolNameOf(JsonElement call)
    {
        var name = ToolName(Str(call, "kind"));
        if (name == "tool" && call.TryGetProperty("_meta", out var meta) && meta.ValueKind == JsonValueKind.Object && Str(meta, "toolName") is { Length: > 0 } real)
            return real;
        return name;
    }

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

    private static double? Number(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : null;

    private static JsonElement Json(JsonNode node) => JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();

    private static JsonElement Json(string text) => JsonDocument.Parse(JsonValue.Create(text).ToJsonString()).RootElement.Clone();

    internal static string Seconds(long millis) => (millis / 1000.0).ToString("0.0", CultureInfo.InvariantCulture);
}

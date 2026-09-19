using System.Text.Json;
using WeaveFleet.Application.Analytics;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode2;

/// <summary>
/// Turns one V2 session's events into Fleet's harness events. V2 streams a turn as
/// <c>session.execution.*</c> (the turn), <c>session.step.*</c> (one model call, one assistant message),
/// <c>session.text.*</c> / <c>session.reasoning.*</c> (a part of that message, by ordinal) and
/// <c>session.tool.*</c> (a tool call in that message, by call id). A question is a form (<c>form.*</c>) that
/// stops the turn on the user. Events Fleet has no use for (permissions are answered by the session, shells,
/// inbox, catalog changes, agent and model switches) map to nothing, as does anything unknown. A subagent call is
/// also read as a delegation (<see cref="TryReadDelegation"/>), since its child session is a Fleet session too.
/// V2 sends no file events on its event stream, so a finished <c>edit</c> or <c>write</c> call also reports the file
/// it wrote.
/// </summary>
/// <param name="workingDirectory">What a relative path in a tool call is relative to, when the event doesn't say.</param>
/// <remarks>One mapper per session, fed from one event stream: not thread-safe.</remarks>
internal sealed class OpenCode2Mapper(string fleetSessionId, string? workingDirectory = null)
{
    /// <summary>V2's tool that runs an agent in a child session (OpenCode's is <c>task</c>).</summary>
    internal const string SubagentTool = "subagent";

    private readonly Dictionary<string, AssistantMessage> _messages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ToolCall> _tools = new(StringComparer.Ordinal);
    private readonly HashSet<string> _questions = new(StringComparer.Ordinal);
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
            "session.tool.input.started" => ToolStarted(data),
            "session.tool.called" => ToolCalled(data),
            "session.tool.progress" => ToolProgress(data),
            "session.tool.success" => ToolEnded(evt, data, failed: false),
            "session.tool.failed" => ToolEnded(evt, data, failed: true),
            "form.created" => QuestionAsked(data),
            "form.replied" or "form.cancelled" => QuestionSettled(data),
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

    /// <summary>
    /// What a <c>subagent</c> tool call says about the delegation it is: the agent and task once the call is made, its
    /// child session once V2 created it (the call's progress, then its result), and how it ended. Read before
    /// <see cref="Map"/>, which forgets a call once it ends.
    /// </summary>
    public OpenCode2Delegation? TryReadDelegation(OpenCode2Event evt)
    {
        if (evt.Type is not ("session.tool.called" or "session.tool.progress" or "session.tool.success" or "session.tool.failed")
            || evt.Data.ValueKind != JsonValueKind.Object
            || ReadToolRef(evt.Data) is not { } call
            || !_tools.TryGetValue(call.CallId, out var tool)
            || tool.Name != SubagentTool)
        {
            return null;
        }

        var input = evt.Type == "session.tool.called" && evt.Data.TryGetProperty("input", out var i) && i.ValueKind == JsonValueKind.Object
            ? i
            : tool.Input ?? default;
        var agent = input.ValueKind == JsonValueKind.Object ? ReadString(input, "agent") ?? ReadString(input, "subagent_type") : null;
        var description = input.ValueKind == JsonValueKind.Object ? ReadString(input, "description") : null;
        var metadata = evt.Data.TryGetProperty("metadata", out var m) && m.ValueKind == JsonValueKind.Object ? m : default;
        var childSessionId = metadata.ValueKind == JsonValueKind.Object ? ReadString(metadata, "sessionID") : null;

        return new OpenCode2Delegation(
            call.CallId,
            string.IsNullOrWhiteSpace(agent) ? SubagentTool : agent,
            string.IsNullOrWhiteSpace(description) ? null : description,
            childSessionId,
            evt.Type switch
            {
                "session.tool.success" => "completed",
                "session.tool.failed" => "error",
                _ => "running",
            });
    }

    /// <summary>
    /// The id Fleet gives part <paramref name="ordinal"/> of kind <paramref name="kind"/> in a V2 message. V2 counts
    /// text and reasoning separately within a step, and a message's history lists them in that order, so history
    /// derives the same ids.
    /// </summary>
    internal static string PartId(string messageId, string kind, int ordinal) => $"{messageId}-{kind}-{ordinal}";

    /// <summary>The id Fleet gives the part for tool call <paramref name="callId"/> in a V2 message.</summary>
    internal static string ToolPartId(string messageId, string callId) => $"{messageId}-tool-{callId}";

    /// <summary>The id Fleet gives file <paramref name="index"/> a tool call returned.</summary>
    internal static string ToolFilePartId(string messageId, string callId, int index) => $"{messageId}-tool-{callId}-file-{index}";

    /// <summary>
    /// What a tool returned, as the text Fleet shows: V2 returns content blocks (a shell's output, then
    /// "Command exited with code 0."), one per line. Files are returned as parts of their own.
    /// </summary>
    internal static string? ToolOutput(IEnumerable<OpenCode2ToolContent>? content)
    {
        var texts = (content ?? []).Where(c => c.Type == "text" && c.Text is not null).Select(c => c.Text!.TrimEnd('\n')).ToList();
        return texts.Count == 0 ? null : string.Join('\n', texts);
    }

    /// <summary>The events that show a V2 message as it is now, for catching up after the event stream was down.</summary>
    /// <remarks>
    /// Parts carry the same ids as live ones, so they replace what the client has. A message that is still
    /// streaming carries on from here with live events.
    /// </remarks>
    public IReadOnlyList<HarnessEvent> MapMessage(OpenCode2Message message)
    {
        if (message is not { Type: "assistant", Id: { } messageId })
            return [];

        var created = message.Time?.Created ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var info = _messages.GetValueOrDefault(messageId)
            ?? new AssistantMessage(created, message.Agent, message.Model?.ProviderId, message.Model?.Id, StepIndex: 0);
        var completed = message.Time?.Completed;
        var tokens = completed is null || message.Tokens is not { } t
            ? null
            : new OpenCode2Tokens { Input = t.Input ?? 0, Output = t.Output ?? 0, Reasoning = t.Reasoning ?? 0 };

        var events = new List<HarnessEvent> { MessageUpdated(messageId, info, completed, completed is null ? null : message.Cost, tokens, message.Finish) };
        int text = 0, reasoning = 0;
        foreach (var content in message.Content ?? [])
        {
            switch (content.Type)
            {
                case "text":
                    events.Add(PartUpdated(TextPart((messageId, PartId(messageId, "text", text++)), "text", content.Text ?? string.Empty)));
                    break;
                case "reasoning":
                    events.Add(PartUpdated(TextPart((messageId, PartId(messageId, "reasoning", reasoning++)), "reasoning", content.Text ?? string.Empty)));
                    break;
                case "tool" when content is { Id: { } callId, Name: { } name, State: { } state }:
                    var status = HistoryToolStatus(state.Status);
                    var input = state.Input.ValueKind == JsonValueKind.Object ? state.Input.Clone() : (JsonElement?)null;
                    events.Add(ToolPart(messageId, callId, name, new OpenCode2ToolPartState
                    {
                        Status = status,
                        Input = input,
                        Output = status is "completed" or "error" && ToolOutput(state.Content) is { } output ? JsonSerializer.SerializeToElement(output, OpenCode2JsonContext.Default.String) : null,
                        Error = status == "error" ? state.Error?.Message ?? state.Error?.Type : null,
                        Metadata = state.Metadata.ValueKind == JsonValueKind.Object ? state.Metadata.Clone() : null,
                    }));
                    events.AddRange(ToolFiles(messageId, callId, state.Content));
                    // A call still running finishes with live events, which name it only by call id.
                    if (status is "pending" or "running")
                        _tools[callId] = new ToolCall(messageId, name, input);
                    break;
            }
        }

        return events;
    }

    /// <summary>
    /// The questions waiting on the user now, for catching up after the event stream was down: the session waits
    /// on the user while any is open.
    /// </summary>
    public IReadOnlyList<HarnessEvent> MapPendingQuestions(IEnumerable<OpenCode2Form> forms)
    {
        var asked = forms.Where(f => f is { IsQuestion: true, Id: not null }).Select(f => f.Id!).ToList();
        var wasWaiting = _questions.Count > 0;
        _questions.Clear();
        _questions.UnionWith(asked);
        return asked.Count > 0 ? [Status(ActivityStatuses.WaitingInput)]
            : wasWaiting ? [Status(ActivityStatuses.Busy)]
            : [];
    }

    /// <summary>True while a question waits on the user.</summary>
    public bool IsWaitingOnQuestion => _questions.Count > 0;

    /// <summary>A tool state's status in history, as Fleet names it (V2's <c>streaming</c> is Fleet's <c>pending</c>).</summary>
    internal static string HistoryToolStatus(string? status) => status switch
    {
        "running" => "running",
        "completed" => "completed",
        "error" => "error",
        _ => "pending",
    };

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

    /// <summary>The model started writing a tool call: the card shows up with the tool's name, input still to come.</summary>
    private List<HarnessEvent> ToolStarted(JsonElement data)
    {
        if (ReadToolRef(data) is not { } call || ReadString(data, "name") is not { } name)
            return [];

        _tools[call.CallId] = new ToolCall(call.MessageId, name, Input: null);
        return [ToolPart(call.MessageId, call.CallId, name, new OpenCode2ToolPartState { Status = "pending" })];
    }

    private List<HarnessEvent> ToolCalled(JsonElement data)
    {
        if (ReadToolRef(data) is not { } call || !_tools.TryGetValue(call.CallId, out var tool))
            return [];

        var input = data.TryGetProperty("input", out var i) && i.ValueKind == JsonValueKind.Object ? i.Clone() : (JsonElement?)null;
        _tools[call.CallId] = tool with { Input = input };
        return [ToolPart(call.MessageId, call.CallId, tool.Name, new OpenCode2ToolPartState { Status = "running", Input = input })];
    }

    /// <summary>
    /// A running subagent call reports its child session, which the card keeps as the call's metadata while it runs
    /// (the result repeats it). Other calls' progress (a shell's id) isn't shown.
    /// </summary>
    private List<HarnessEvent> ToolProgress(JsonElement data)
    {
        if (ReadToolRef(data) is not { } call
            || !_tools.TryGetValue(call.CallId, out var tool)
            || tool.Name != SubagentTool
            || !data.TryGetProperty("metadata", out var metadata)
            || metadata.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        return [ToolPart(call.MessageId, call.CallId, tool.Name, new OpenCode2ToolPartState
        {
            Status = "running",
            Input = tool.Input,
            Metadata = metadata.Clone(),
        })];
    }

    /// <summary>
    /// <c>session.tool.success</c> and <c>session.tool.failed</c>: the call's result, any files it returned, and for a
    /// file it wrote, the file.
    /// </summary>
    private List<HarnessEvent> ToolEnded(OpenCode2Event evt, JsonElement data, bool failed)
    {
        if (ReadToolRef(data) is not { } call || !_tools.Remove(call.CallId, out var tool))
            return [];

        var content = data.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array
            ? c.Deserialize(OpenCode2JsonContext.Default.ListOpenCode2ToolContent)
            : null;
        var output = ToolOutput(content);
        var events = new List<HarnessEvent>
        {
            ToolPart(call.MessageId, call.CallId, tool.Name, new OpenCode2ToolPartState
            {
                Status = failed ? "error" : "completed",
                Input = tool.Input,
                Output = output is null ? null : JsonSerializer.SerializeToElement(output, OpenCode2JsonContext.Default.String),
                Error = failed ? ReadError(data).Message : null,
                Metadata = data.TryGetProperty("metadata", out var m) && m.ValueKind == JsonValueKind.Object ? m.Clone() : null,
            }),
        };
        events.AddRange(ToolFiles(call.MessageId, call.CallId, content));
        if (!failed)
            events.AddRange(FileWritten(call.MessageId, tool, evt.Location?.Directory));
        return events;
    }

    /// <summary>V2's tools that write a file, both naming it in <c>input.path</c>.</summary>
    private static readonly HashSet<string> FileWritingTools = new(StringComparer.Ordinal) { "edit", "write" };

    /// <summary>
    /// A finished <c>edit</c> or <c>write</c> call as Fleet's two file events: <c>files.written</c> (the files a tool
    /// call wrote, which progress tracking reads) and <c>file.watcher.updated</c> (which Fleet turns into
    /// <c>files.changed</c>, so open files and the file list reload). OpenCode (1.x) gets the second from its file
    /// watcher; V2 has one too but doesn't send it on the event stream. The path is made absolute, as the watcher's
    /// are, against the event's folder. Edits by other means (a shell's <c>sed</c>) aren't seen; the client reloads open
    /// files when the turn ends for those.
    /// </summary>
    private List<HarnessEvent> FileWritten(string messageId, ToolCall tool, string? directory)
    {
        if (!FileWritingTools.Contains(tool.Name)
            || tool.Input is not { ValueKind: JsonValueKind.Object } input
            || ReadString(input, "path") is not { Length: > 0 } path)
        {
            return [];
        }

        var baseDirectory = directory ?? workingDirectory;
        if (!Path.IsPathRooted(path) && string.IsNullOrWhiteSpace(baseDirectory))
            return [];

        var fullPath = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(baseDirectory!, path));
        return
        [
            Event(EventTypes.FilesWritten, JsonSerializer.SerializeToElement(
                new FilesWrittenPayload { SessionId = fleetSessionId, MessageId = messageId, Paths = [fullPath] },
                OpenCode2JsonContext.Default.FilesWrittenPayload)),
            Event(EventTypes.FileWatcherUpdated, JsonSerializer.SerializeToElement(
                new OpenCode2FileChangedPayload { File = fullPath, Event = "change" },
                OpenCode2JsonContext.Default.OpenCode2FileChangedPayload)),
        ];
    }

    private IEnumerable<HarnessEvent> ToolFiles(string messageId, string callId, IEnumerable<OpenCode2ToolContent>? content)
        => (content ?? [])
            .Where(c => c.Type == "file" && c.Uri is not null)
            .Select((file, index) => PartUpdated(new OpenCode2Part
            {
                Type = "file",
                Id = ToolFilePartId(messageId, callId, index),
                SessionId = fleetSessionId,
                MessageId = messageId,
                Mime = file.Mime ?? "application/octet-stream",
                Url = file.Uri,
                Filename = file.Name,
            }));

    private HarnessEvent ToolPart(string messageId, string callId, string name, OpenCode2ToolPartState state)
        => PartUpdated(new OpenCode2Part
        {
            Type = "tool",
            Id = ToolPartId(messageId, callId),
            SessionId = fleetSessionId,
            MessageId = messageId,
            Tool = name,
            CallId = callId,
            State = state,
        });

    /// <summary>
    /// The question tool asks with a form, and the turn waits on the user until it's answered or dismissed. The
    /// question itself shows from the tool call's input, like OpenCode's (1.x) question tool.
    /// </summary>
    private List<HarnessEvent> QuestionAsked(JsonElement data)
    {
        if (!data.TryGetProperty("form", out var f) || f.ValueKind != JsonValueKind.Object)
            return [];

        var form = f.Deserialize(OpenCode2JsonContext.Default.OpenCode2Form);
        if (form is not { IsQuestion: true, Id: { } formId })
            return [];

        _questions.Add(formId);
        return [Status(ActivityStatuses.WaitingInput)];
    }

    /// <summary>An answered question lets the turn go on; a dismissed one ends it (V2 interrupts the turn next).</summary>
    private List<HarnessEvent> QuestionSettled(JsonElement data)
        => ReadString(data, "id") is { } formId && _questions.Remove(formId)
            ? [Status(ActivityStatuses.Busy)]
            : [];

    private static (string MessageId, string CallId)? ReadToolRef(JsonElement data)
        => ReadString(data, "assistantMessageID") is { } messageId && ReadString(data, "id") is { } callId
            ? (messageId, callId)
            : null;

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

    /// <summary>A tool call from its first event to its result: later events name it only by call id.</summary>
    private sealed record ToolCall(string MessageId, string Name, JsonElement? Input);
}

/// <summary>
/// A subagent call as Fleet's delegation: <paramref name="Agent"/> is its title, <paramref name="ChildSessionId"/> the
/// V2 session it runs in (once V2 said), and <paramref name="Status"/> <c>running</c>, <c>completed</c> or <c>error</c>.
/// </summary>
internal sealed record OpenCode2Delegation(
    string ToolCallId,
    string Agent,
    string? Description,
    string? ChildSessionId,
    string Status);

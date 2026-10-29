using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Sessions.Creation;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Users;

namespace WeaveFleet.Infrastructure.Harnesses.Acp;

/// <summary>
/// Subagents and background work. An update tagged with <c>_meta.parentToolCallId</c> belongs to the subagent a <c>task</c>
/// call started: it goes to a conversation of its own (served through a hidden child session, see <see cref="AcpChildSession"/>)
/// and never to the parent's. <c>_fleet/work</c> says what started and ended, which is what Fleet's Working strip shows.
/// </summary>
internal sealed partial class AcpHarnessSession
{
    /// <summary>The code of a JSON-RPC "method not found": an older weave-copilot doesn't know the work requests.</summary>
    private const int MethodNotFound = -32601;

    /// <summary>One subagent's conversation. It has no mapper until its Fleet session is known (live), or at once while a load replays.</summary>
    private sealed class ChildConversation
    {
        public AcpMapper? Mapper;
        public string? FleetId;
        public bool Busy;
        public bool Background;
        public readonly List<JsonElement> Waiting = [];
    }

    private readonly Lock _childLock = new();
    private readonly Dictionary<string, ChildConversation> _children = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WorkReport> _running = new(StringComparer.Ordinal);
    private readonly IServiceScopeFactory? _scopes;
    private Func<string, string, string, Task<string?>>? _childSessions;
    private Task _childWork = Task.CompletedTask;

    /// <summary>
    /// Makes (or finds) the hidden Fleet session a subagent's conversation goes to: under Fleet session <c>parent</c>, for the
    /// subagent called with tool call <c>callId</c>, titled <c>title</c>. Returns its id, or null when it can't be made.
    /// Fleet's <see cref="SessionCreation.EnsureDelegatedChildSessionAsync"/> unless a test says otherwise.
    /// </summary>
    internal Func<string, string, string, Task<string?>> ChildSessions
    {
        get => _childSessions ?? MakeChildSessionAsync;
        init => _childSessions = value;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't make a child session for subagent {CallId} of {SessionId}")]
    private static partial void LogChildSessionFailed(ILogger logger, Exception ex, string callId, string sessionId);

    private async Task<string?> MakeChildSessionAsync(string parentFleetSessionId, string callId, string title)
    {
        if (_scopes is null)
            return null;
        using var userScope = BackgroundUserContext.BeginScope(_ownerUserId);
        using var scope = _scopes.CreateScope();
        if (scope.ServiceProvider.GetService<SessionCreation>() is not { } creation)
            return null;
        var child = await creation.EnsureDelegatedChildSessionAsync(parentFleetSessionId, callId, title).ConfigureAwait(false);
        return child.IsSuccess ? child.Value.Id : null;
    }

    /// <summary>Takes what is about subagents and work; false for everything else, which the parent's mapper handles.</summary>
    private bool HandleChildTraffic(string method, JsonElement parameters)
    {
        if (method == "_fleet/work")
        {
            OnWork(parameters);
            return true;
        }

        if (method == "session/update" && parameters.TryGetProperty("update", out var update)
            && update.TryGetProperty("_meta", out var meta) && meta.ValueKind == JsonValueKind.Object
            && AcpMapper.Str(meta, "parentToolCallId") is { Length: > 0 } call)
        {
            ToChild(call, update);
            return true;
        }

        return false;
    }

    private ChildConversation Child(string callId)
    {
        if (!_children.TryGetValue(callId, out var child))
            _children[callId] = child = new ChildConversation();
        return child;
    }

    private void ToChild(string callId, JsonElement update)
    {
        lock (_childLock)
        {
            var child = Child(callId);
            // A replay emits nothing, so the call id does for the mapper's session id.
            if (child.Mapper is null && _replaying)
                child.Mapper = new AcpMapper(callId);
            if (child.Mapper is null)
            {
                child.Waiting.Add(update.Clone());
                return;
            }

            var events = child.Mapper.Update(update);
            if (!_replaying)
                EmitFor(child, events);
        }
    }

    /// <summary>A child's events, named for its Fleet session; none while it has none.</summary>
    private void EmitFor(ChildConversation child, IEnumerable<HarnessEvent> events)
    {
        if (child.FleetId is not { } id)
            return;
        foreach (var evt in events)
            Emit(evt with { FleetSessionId = id });
    }

    /// <summary>The conversation of the subagent called with <paramref name="callId"/>, as far as this session has seen it.</summary>
    internal IReadOnlyList<HarnessMessage> ChildMessages(string callId)
    {
        AcpMapper? mapper;
        lock (_childLock)
            mapper = _children.GetValueOrDefault(callId)?.Mapper;
        return mapper?.Messages() ?? [];
    }

    private static bool? Flag(JsonElement p, string name)
        => p.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    private void OnWork(JsonElement p)
    {
        if (AcpMapper.Str(p, "workId") is not { Length: > 0 } id)
            return;
        lock (_childLock)
        {
            if (AcpMapper.Str(p, "event") != "ended")
            {
                var kind = AcpMapper.Str(p, "kind");
                var report = new WorkReport
                {
                    WorkId = id,
                    Kind = WorkKinds.IsKnown(kind) ? kind : WorkKinds.Task,
                    Title = AcpMapper.Str(p, "title"),
                    Label = AcpMapper.Str(p, "label"),
                    ToolCallId = AcpMapper.Str(p, "toolCallId"),
                    Background = Flag(p, "background"),
                    ChildHarnessSessionId = kind == WorkKinds.Subagent ? id : null,
                    CanStop = Flag(p, "canStop"),
                    CanReadOutput = Flag(p, "canReadOutput"),
                };
                _running[id] = report;
                Emit(WorkEvents.Started(report, _fleetSessionId));
                if (kind == WorkKinds.Subagent)
                    Later(() => StartChildAsync(report));
                return;
            }

            if (!_running.Remove(id, out var known))
                return;
            var reason = AcpMapper.Str(p, "endedReason") is { } said && WorkEndedReasons.IsKnown(said) ? said : WorkEndedReasons.Completed;
            Emit(WorkEvents.Ended(known with { Detail = AcpMapper.Str(p, "detail") ?? known.Detail }, reason, _fleetSessionId));
            if (known.Kind == WorkKinds.Subagent)
                Later(() => EndChildAsync(id, reason));
        }
    }

    /// <summary>Child work runs one step after another, so a quick subagent can't end before its session is made.</summary>
    private void Later(Func<Task> work)
    {
        lock (_childLock)
            _childWork = _childWork.ContinueWith(_ => work(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
    }

    /// <summary>Makes the child's session, marks it busy, and gives it what the subagent said while it was being made.</summary>
    private async Task StartChildAsync(WorkReport report)
    {
        string? id = null;
        try
        {
            id = await ChildSessions(_fleetSessionId, report.WorkId, report.Label ?? report.Title ?? "subagent").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogChildSessionFailed(_logger, ex, report.WorkId, _fleetSessionId);
        }

        lock (_childLock)
        {
            var child = Child(report.WorkId);
            child.Background = report.Background == true;
            child.FleetId = id;
            child.Mapper ??= new AcpMapper(id ?? report.WorkId);
            if (id is null)
            {
                child.Waiting.Clear();
                return;
            }

            child.Busy = true;
            EmitFor(child, [child.Mapper.Status(ActivityStatuses.Busy)]);
            foreach (var update in child.Waiting)
                EmitFor(child, child.Mapper.Update(update));
            child.Waiting.Clear();
        }
    }

    /// <summary>The subagent is done: its turn ends, so the child is idle and none of its calls reads as running.</summary>
    private Task EndChildAsync(string callId, string? reason)
    {
        lock (_childLock)
        {
            if (_children.GetValueOrDefault(callId) is not { Busy: true, Mapper: { } mapper } child)
                return Task.CompletedTask;
            child.Busy = false;
            EmitFor(child, mapper.EndTurn(reason == WorkEndedReasons.Cancelled ? "cancelled" : null));
        }

        return Task.CompletedTask;
    }

    /// <summary>The parent's turn is over, and so is any subagent it waited for.</summary>
    private void EndSyncChildren()
    {
        lock (_childLock)
        {
            foreach (var (callId, child) in _children)
            {
                if (child.Busy && !child.Background)
                    Later(() => EndChildAsync(callId, null));
            }
        }
    }

    /// <summary>A relaunch replaced the process that ran the work: none of it will report back, so it ends as lost.</summary>
    private void LoseRunningWork()
    {
        lock (_childLock)
        {
            foreach (var report in _running.Values)
            {
                Emit(WorkEvents.Ended(report, WorkEndedReasons.Lost, _fleetSessionId));
                if (report.Kind == WorkKinds.Subagent)
                    Later(() => EndChildAsync(report.WorkId, WorkEndedReasons.Lost));
            }

            _running.Clear();
        }
    }

    /// <inheritdoc />
    public async Task<bool> StopWorkAsync(string workId, CancellationToken ct)
    {
        var answer = await WorkRequestAsync("_fleet/work/stop", workId, null, ct).ConfigureAwait(false);
        return answer.ValueKind == JsonValueKind.Object && answer.TryGetProperty("stopped", out var stopped) && stopped.ValueKind == JsonValueKind.True;
    }

    /// <inheritdoc />
    public async Task<WorkOutput?> ReadWorkOutputAsync(string workId, long offset, CancellationToken ct)
    {
        var answer = await WorkRequestAsync("_fleet/work/output", workId, offset, ct).ConfigureAwait(false);
        if (answer.ValueKind != JsonValueKind.Object || AcpMapper.Str(answer, "text") is not { } text)
            return null;
        long Number(string name, long fallback) => answer.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : fallback;
        var next = Number("nextOffset", offset + System.Text.Encoding.UTF8.GetByteCount(text));
        return new WorkOutput(text, next, Number("size", next), Flag(answer, "truncated") == true);
    }

    private async Task<JsonElement> WorkRequestAsync(string method, string workId, long? offset, CancellationToken ct)
    {
        var request = new JsonObject { ["sessionId"] = ResumeToken, ["workId"] = workId };
        if (offset is { } from)
            request["offset"] = from;
        try
        {
            return await _agent.Client.RequestAsync(method, request, ct).ConfigureAwait(false);
        }
        catch (AcpException ex) when (ex.Code == MethodNotFound)
        {
            throw new NotSupportedException("This version of weave-copilot can't do that: update it from Settings.", ex);
        }
    }

    /// <inheritdoc />
    /// <remarks>What this session saw start and not end: empty after a restart, so Fleet ends the old work as lost.</remarks>
    public Task<IReadOnlyList<WorkReport>?> GetRunningWorkAsync(CancellationToken ct)
    {
        lock (_childLock)
            return Task.FromResult<IReadOnlyList<WorkReport>?>([.. _running.Values]);
    }
}

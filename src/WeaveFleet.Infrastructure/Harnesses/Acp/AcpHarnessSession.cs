using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Analytics;
using WeaveFleet.Application.FleetTools;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses.Acp;

/// <summary>
/// A Fleet session on an ACP agent, one agent process per session. Prompts run as a long <c>session/prompt</c> request
/// whose updates stream into Fleet's events; permission asks wait for the user; a restart loads the session again with
/// <c>session/load</c>, which replays the conversation.
/// </summary>
internal sealed partial class AcpHarnessSession : IHarnessSession
{
    /// <summary>weave-copilot's answer to a <c>_fleet/steer</c> that arrived after the turn ended.</summary>
    internal const int NoTurnRunning = -32001;

    private readonly IAcpAgent _agent;
    private readonly AcpMapper _mapper;
    private readonly string _fleetSessionId;
    private readonly string _workingDirectory;
    private readonly ILogger _logger;
    private readonly Channel<HarnessEvent> _events = Channel.CreateBounded<HarnessEvent>(new BoundedChannelOptions(2000)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
    });

    private readonly PermissionGate _permissions = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _asks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, JsonElement> _askOptions = new(StringComparer.Ordinal);
    private IReadOnlyList<ModelInfo> _models = [];
    private string? _model;
    private readonly IAnalyticsCollector? _analytics;
    private readonly string? _projectId;
    private readonly string? _projectName;
    private readonly string _ownerUserId;
    private bool _replaying;
    private bool _disposed;
    private int _ended;

    internal AcpHarnessSession(string harnessType, string instanceId, string fleetSessionId, string acpSessionId, string workingDirectory,
        IAcpAgent agent, ILogger logger, IAnalyticsCollector? analytics = null, string? projectId = null, string? projectName = null,
        string ownerUserId = "local-user", FleetToolCallRecords? fleetToolCalls = null)
    {
        _analytics = analytics;
        _projectId = projectId;
        _projectName = projectName;
        _ownerUserId = ownerUserId;
        HarnessType = harnessType;
        InstanceId = instanceId;
        _fleetSessionId = fleetSessionId;
        ResumeToken = acpSessionId;
        _workingDirectory = workingDirectory;
        _agent = agent;
        _logger = logger;
        // A replayed call is an old one: what Fleet keeps now is for the calls of the turn running.
        _mapper = new AcpMapper(fleetSessionId, (tool, arguments) => _replaying ? null : fleetToolCalls?.Take(fleetSessionId, tool, arguments));
        agent.Client.Notification += OnNotification;
        agent.Client.RequestHandler = OnRequestAsync;
        agent.Client.Closed += OnClosed;
    }

    /// <summary>Runs once when the session ends, for whoever issued it something that must not outlive it.</summary>
    internal Action? OnEnded { get; init; }

    private void End()
    {
        if (Interlocked.Exchange(ref _ended, 1) == 0)
            OnEnded?.Invoke();
    }

    /// <summary>An archived session's agent can no longer call back into Fleet.</summary>
    public Task ArchiveAsync(CancellationToken ct)
    {
        End();
        return Task.CompletedTask;
    }

    public string InstanceId { get; }

    public int? ProcessId => _agent.ProcessId;

    public string? ResumeToken { get; }

    public string HarnessType { get; }

    public HarnessSessionStatus Status { get; private set; } = HarnessSessionStatus.Idle;

    /// <summary>The models the agent offered (<c>session/new</c>'s <c>models</c>), and the one in use.</summary>
    internal IReadOnlyList<ModelInfo> Models => _models;

    /// <summary>Takes the model list a <c>session/new</c> or <c>session/load</c> result carries.</summary>
    internal void ReadModels(JsonElement result)
    {
        (_models, _model) = ParseModels(result);
    }

    /// <summary>The models in a <c>session/new</c> result and the current one.</summary>
    internal static (IReadOnlyList<ModelInfo> Models, string? Current) ParseModels(JsonElement result)
    {
        if (!result.TryGetProperty("models", out var models))
            return ([], null);
        var list = new List<ModelInfo>();
        if (models.TryGetProperty("availableModels", out var available) && available.ValueKind == JsonValueKind.Array)
        {
            foreach (var model in available.EnumerateArray())
            {
                if (AcpMapper.Str(model, "modelId") is { } id && list.All(m => m.Id != id))
                    list.Add(new ModelInfo { Id = id, Name = AcpMapper.Str(model, "name") ?? id });
            }
        }

        return (list, AcpMapper.Str(models, "currentModelId"));
    }

    /// <summary>The models of a <c>_fleet/models</c> result (<c>models</c>: <c>[{modelId, name}]</c>) and the current one.</summary>
    internal static (IReadOnlyList<ModelInfo> Models, string? Current) ParseModelList(JsonElement result)
    {
        var list = new List<ModelInfo>();
        if (result.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
        {
            foreach (var model in models.EnumerateArray())
            {
                if (AcpMapper.Str(model, "modelId") is { } id && list.All(m => m.Id != id))
                    list.Add(new ModelInfo { Id = id, Name = AcpMapper.Str(model, "name") ?? id });
            }
        }

        return (list, AcpMapper.Str(result, "currentModelId"));
    }

    /// <summary>Loads the conversation again from the agent, which replays it as updates; none of them reach Fleet as events.</summary>
    internal async Task LoadAsync(JsonArray? mcpServers, CancellationToken ct)
    {
        _replaying = true;
        try
        {
            var result = await _agent.Client.RequestAsync("session/load",
                new JsonObject { ["sessionId"] = ResumeToken, ["cwd"] = _workingDirectory, ["mcpServers"] = mcpServers ?? [] }, ct).ConfigureAwait(false);
            ReadModels(result);
        }
        finally
        {
            _replaying = false;
        }
    }

    public async Task SendPromptAsync(string text, PromptOptions? options, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var images = options?.Attachments ?? [];
        if (images.Any(a => !a.Mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)))
            throw new NotSupportedException("GitHub Copilot sessions take images, not other kinds of file.");

        if (Status == HarnessSessionStatus.Running)
        {
            if (options?.Delivery != PromptDelivery.Steer)
                throw new HarnessBusyException("The agent is still working on the last prompt.");
            if (images.Count > 0)
                throw new NotSupportedException("An image can't steer a turn that's running: send it when the turn is over.");
            try
            {
                await _agent.Client.RequestAsync("_fleet/steer", new JsonObject { ["sessionId"] = ResumeToken, ["prompt"] = text }, ct).ConfigureAwait(false);
            }
            catch (AcpException ex) when (ex.Code == NoTurnRunning)
            {
                // The turn ended while this was on its way: Fleet queues it as a prompt of its own.
                throw new HarnessBusyException("The turn had just ended, so the message can't steer it.");
            }

            Emit(_mapper.UserPrompt(text, messageId: options.MessageId));
            return;
        }

        if (options?.ModelId is { Length: > 0 } wanted && ModelId(wanted) is { } model && model != _model)
        {
            await _agent.Client.RequestAsync("session/set_model", new JsonObject { ["sessionId"] = ResumeToken, ["modelId"] = model }, ct).ConfigureAwait(false);
            _model = model;
        }

        Status = HarnessSessionStatus.Running;
        Emit(_mapper.UserPrompt(text, images, options?.MessageId));
        Emit(_mapper.Status(ActivityStatuses.Busy));
        _ = RunPromptAsync(text, images);
    }

    private async Task RunPromptAsync(string text, IReadOnlyList<HarnessAttachment> images)
    {
        try
        {
            var blocks = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text });
            foreach (var image in images)
                blocks.Add((JsonNode)new JsonObject { ["type"] = "image", ["data"] = image.Data, ["mimeType"] = image.Mime, ["uri"] = image.Filename });
            var result = await _agent.Client.RequestAsync("session/prompt", new JsonObject { ["sessionId"] = ResumeToken, ["prompt"] = blocks }, CancellationToken.None).ConfigureAwait(false);
            Status = HarnessSessionStatus.Idle;
            if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                Emit(_mapper.Usage(usage));
                ReportTokens(usage);
            }

            Emit(_mapper.EndTurn(AcpMapper.Str(result, "stopReason")));
        }
        catch (AcpException ex)
        {
            var alive = !_agent.Client.IsClosed;
            Status = alive ? HarnessSessionStatus.Idle : HarnessSessionStatus.Error;
            var why = alive || _agent.Stderr.Length == 0 ? ex.Message : $"{ex.Message} {_agent.Stderr}";
            Emit(_mapper.Fail(why));
        }
    }

    /// <summary>
    /// What the turn used goes to the analytics collector, which adds it to the session's tokens (the status bar's count).
    /// The agent's input counts the cached tokens, which Fleet counts apart.
    /// </summary>
    private void ReportTokens(JsonElement usage)
    {
        if (_analytics is null)
            return;
        double Count(string name) => usage.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) ? Math.Max(0, n) : 0;
        var cacheRead = Count("cachedReadTokens");
        var cacheWrite = Count("cachedWriteTokens");
        var reasoning = Count("thoughtTokens");
        var input = Math.Max(0, Count("inputTokens") - cacheRead - cacheWrite);
        var output = Math.Max(0, Count("outputTokens") - reasoning);
        if (input + output + reasoning + cacheRead + cacheWrite <= 0)
            return;
        _analytics.AcceptTokenEvent(new TokenEventData(
            EventId: $"{_fleetSessionId}:{Guid.NewGuid():N}",
            SessionId: _fleetSessionId,
            ProjectId: _projectId,
            ProjectName: _projectName,
            WorkspaceDirectory: _workingDirectory,
            ModelId: _model,
            ProviderId: "copilot",
            TokensInput: input,
            TokensOutput: output,
            TokensReasoning: reasoning,
            TokensCacheRead: cacheRead,
            TokensCacheWrite: cacheWrite,
            TokensTotal: input + output + reasoning + cacheRead + cacheWrite,
            Cost: 0,
            EstimatedCost: null,
            CreatedAt: DateTimeOffset.UtcNow,
            UserId: _ownerUserId));
    }

    private static string ModelId(string id) => id.Contains('/') ? id[(id.LastIndexOf('/') + 1)..] : id;

    /// <summary>The list Copilot last reported, kept by the mapper (it also fills while a loaded conversation replays).</summary>
    public Task<IReadOnlyList<TodoEntry>?> GetTodosAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<TodoEntry>?>(_mapper.Todos());

    public async Task AbortAsync(CancellationToken ct)
    {
        foreach (var id in _asks.Keys)
            Settle(id, PermissionReplies.Reject, "cancelled");
        await _agent.Client.NotifyAsync("session/cancel", new JsonObject { ["sessionId"] = ResumeToken }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task BindPermissionRulesAsync(IPermissionRules rules, CancellationToken ct) => _permissions.BindAsync(rules, ct);

    public Task ApplyPermissionsAsync(PermissionPolicy policy, CancellationToken ct)
    {
        _permissions.Policy = policy;
        return Task.CompletedTask;
    }

    public async Task ReplyToPermissionAsync(string requestId, string reply, string? message, CancellationToken ct)
    {
        var entry = _permissions.Pending.Get(requestId)
            ?? throw new KeyNotFoundException($"Permission request '{requestId}' isn't waiting for an answer.");
        if (reply == PermissionReplies.Always)
            await _permissions.AllowFromNowOnAsync(entry.Ask, ct).ConfigureAwait(false);
        Settle(requestId, reply, null);
    }

    /// <summary>The agent's <c>session/request_permission</c>: answered by the level at once, or by the user when they get to it.</summary>
    private async Task<JsonNode?> OnRequestAsync(string method, JsonElement parameters)
    {
        if (method != "session/request_permission")
            throw new AcpException($"Fleet doesn't handle {method}.", -32601);

        var call = parameters.GetProperty("toolCall");
        var tool = AcpMapper.ToolNameOf(call);
        if (_permissions.Decide(tool, []) is { } decision)
            return Outcome(parameters, decision);

        var id = Guid.NewGuid().ToString("N");
        var ask = new PermissionAsk
        {
            Id = id,
            SessionId = _fleetSessionId,
            Kind = PermissionKinds.Classify(tool),
            Tool = tool,
            Title = AcpMapper.Str(call, "title"),
            CallId = AcpMapper.Str(call, "toolCallId"),
            Directory = _workingDirectory,
        };
        var answer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _asks[id] = answer;
        _permissions.Pending.Add(ask, _fleetSessionId);
        Emit(PermissionEvents.Asked(ask, _fleetSessionId));
        Emit(_mapper.Status(ActivityStatuses.WaitingInput));
        var reply = await answer.Task.ConfigureAwait(false);
        return Outcome(parameters, reply);
    }

    /// <summary>The ACP answer for Fleet's: the option of the matching kind, or a cancelled outcome when there is none.</summary>
    private static JsonObject Outcome(JsonElement request, string reply)
    {
        var wanted = reply switch { PermissionReplies.Once => "allow_once", PermissionReplies.Always => "allow_always", _ => "reject_once" };
        if (request.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array)
        {
            foreach (var option in options.EnumerateArray())
            {
                if (AcpMapper.Str(option, "kind") == wanted && AcpMapper.Str(option, "optionId") is { } optionId)
                    return new JsonObject { ["outcome"] = new JsonObject { ["outcome"] = "selected", ["optionId"] = optionId } };
            }
        }

        return new JsonObject { ["outcome"] = new JsonObject { ["outcome"] = "cancelled" } };
    }

    private void Settle(string id, string reply, string? _)
    {
        if (!_asks.TryRemove(id, out var answer))
            return;
        answer.TrySetResult(reply);
        if (_permissions.Pending.Remove(id) is { } settled)
            Emit(PermissionEvents.Replied(settled.Ask, reply, _fleetSessionId));
        if (!_permissions.Pending.Any && Status == HarnessSessionStatus.Running)
            Emit(_mapper.Status(ActivityStatuses.Busy));
    }

    private void OnNotification(string method, JsonElement parameters)
    {
        IReadOnlyList<HarnessEvent> events = method switch
        {
            "session/update" when parameters.TryGetProperty("update", out var update) => _mapper.Update(update),
            "_fleet/fusion" => _mapper.Fusion(parameters),
            _ => [],
        };
        if (!_replaying)
            Emit(events);
    }

    private void OnClosed()
    {
        End();
        if (_disposed)
            return;
        Status = HarnessSessionStatus.Error;
        foreach (var id in _asks.Keys)
            Settle(id, PermissionReplies.Gone, null);
    }

    private void Emit(HarnessEvent evt) => _events.Writer.TryWrite(evt);

    private void Emit(IEnumerable<HarnessEvent> events)
    {
        foreach (var evt in events)
            Emit(evt);
    }

    public Task<MessagePage> GetMessagesAsync(MessageQuery? query, CancellationToken ct)
    {
        var messages = _mapper.Messages().ToList();
        if (!string.IsNullOrWhiteSpace(query?.Before) && messages.FindIndex(m => m.Id == query.Before) is >= 0 and var at)
            messages = messages[..at];
        var more = query?.Limit is { } limit && messages.Count > limit;
        if (more)
            messages = messages[^query!.Limit!.Value..];
        return Task.FromResult(new MessagePage(messages, more));
    }

    public async IAsyncEnumerable<HarnessEvent> SubscribeAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var evt in _events.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return evt;
    }

    public Task WaitForEventSubscriptionAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<HealthCheckResult> CheckHealthAsync(CancellationToken ct)
        => Task.FromResult(_agent.IsRunning ? new HealthCheckResult(true, null) : new HealthCheckResult(false, "The GitHub Copilot process is not running."));

    public Task<string?> GetActivityStatusAsync(CancellationToken ct) => Task.FromResult<string?>(null);

    public Task<IReadOnlyList<ProviderInfo>> GetProvidersAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ProviderInfo>>([new ProviderInfo { Id = "copilot", Name = "GitHub Copilot", Models = _models }]);

    public Task<IReadOnlyList<AgentInfo>> GetAgentsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<AgentInfo>>([]);

    public Task<IReadOnlyList<CommandInfo>> GetCommandsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<CommandInfo>>([]);

    public Task<string?> AskOffTheRecordAsync(string prompt, CancellationToken ct) => Task.FromResult<string?>(null);

    public Task<string?> SendCommandAsync(CommandOptions options, CancellationToken ct)
        => throw new NotSupportedException("GitHub Copilot sessions don't take slash commands yet.");

    public Task AnswerQuestionAsync(string requestId, IReadOnlyList<IReadOnlyList<string>> answers, CancellationToken ct)
        => throw new NotSupportedException("GitHub Copilot sessions don't ask questions yet.");

    public Task RejectQuestionAsync(string requestId, CancellationToken ct)
        => throw new NotSupportedException("GitHub Copilot sessions don't ask questions yet.");

    public async Task StopAsync(CancellationToken ct)
    {
        if (_disposed)
            return;
        Status = HarnessSessionStatus.Stopped;
        await DisposeAsync().ConfigureAwait(false);
    }

    public Task DeleteAsync(CancellationToken ct) => StopAsync(ct);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        End();
        foreach (var id in _asks.Keys)
            Settle(id, PermissionReplies.Gone, null);
        _events.Writer.TryComplete();
        await _agent.DisposeAsync().ConfigureAwait(false);
    }
}

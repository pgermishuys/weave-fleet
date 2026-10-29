using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Analytics;
using WeaveFleet.Application.FleetTools;
using WeaveFleet.Application.Harnesses;
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

    private IAcpAgent _agent;
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
    private string? _selectedAgent;
    private readonly IAnalyticsCollector? _analytics;
    private readonly string? _projectId;
    private readonly string? _projectName;
    private readonly string _ownerUserId;
    private bool _replaying;
    // A new process is loading the conversation the mapper already has: what it replays is dropped, and its ending
    // (it failed to start) doesn't end the session, which carries on with the process it had.
    private bool _relaunching;
    private readonly SemaphoreSlim _relaunch = new(1, 1);
    private static readonly TimeSpan RelaunchWait = TimeSpan.FromSeconds(30);
    private bool _compacting;
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
        _mapper = new AcpMapper(fleetSessionId, (tool, arguments) => _replaying ? null : fleetToolCalls?.Take(fleetSessionId, tool, arguments), () => _model);
        Attach(agent);
    }

    private void Attach(IAcpAgent agent)
    {
        agent.Client.Notification += OnNotification;
        agent.Client.RequestHandler = OnRequestAsync;
        agent.Client.Closed += OnClosed;
    }

    private void Detach(IAcpAgent agent)
    {
        agent.Client.Notification -= OnNotification;
        agent.Client.RequestHandler = null;
        agent.Client.Closed -= OnClosed;
    }

    /// <summary>The <c>mcpServers</c> the session's process was started with, which a new one is given again.</summary>
    internal JsonArray? McpServers { get; set; }

    /// <summary>Fleet's folder of skills the session's process was given, which a new one is given again.</summary>
    internal string? SkillDirectory { get; set; }

    /// <summary>Which of Fleet's tools the process listed when it started; null where Fleet can't tell.</summary>
    internal FleetToolSwitches? FleetTools { get; set; }

    /// <summary>Which of Fleet's tools the session should have now; null where Fleet can't tell (tests).</summary>
    internal Func<Task<FleetToolSwitches?>>? ReadFleetTools { get; init; }

    /// <summary>Starts a new agent process for the session, as the first one was; null where the session can't restart (tests).</summary>
    internal Func<CancellationToken, Task<IAcpAgent>>? Relaunch { get; init; }

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

    /// <summary>
    /// Loads the conversation again from the agent, which replays it as updates; none of them reach Fleet as events.
    /// <paramref name="skillDirectory"/> is Fleet's folder of skills, which a loaded session needs again.
    /// </summary>
    internal async Task LoadAsync(JsonArray? mcpServers, CancellationToken ct, string? skillDirectory = null)
    {
        McpServers = mcpServers;
        SkillDirectory = skillDirectory;
        _replaying = true;
        try
        {
            ReadModels(await LoadOnAsync(_agent, ct).ConfigureAwait(false));
        }
        finally
        {
            _replaying = false;
        }
    }

    private Task<JsonElement> LoadOnAsync(IAcpAgent agent, CancellationToken ct)
    {
        var load = new JsonObject { ["sessionId"] = ResumeToken, ["cwd"] = _workingDirectory, ["mcpServers"] = McpServers?.DeepClone() ?? new JsonArray() };
        if (SkillDirectoriesMeta(SkillDirectory) is { } meta)
            load["_meta"] = meta;
        return agent.Client.RequestAsync("session/load", load, ct);
    }

    /// <summary>
    /// Copilot lists the tools of Fleet's MCP server once, when its process starts, so a switch that changed since
    /// (Settings → Browser, memory, messages between sessions) takes a new process, which loads the conversation again.
    /// When that fails, the session carries on with the process it has.
    /// </summary>
    private async Task RefreshFleetToolsAsync(CancellationToken ct)
    {
        if (Relaunch is null || ReadFleetTools is null)
            return;

        await _relaunch.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (await ReadFleetTools().ConfigureAwait(false) is { } wanted && wanted != FleetTools)
                await RelaunchAsync(wanted, ct).ConfigureAwait(false);
        }
        finally
        {
            _relaunch.Release();
        }
    }

    private async Task RelaunchAsync(FleetToolSwitches wanted, CancellationToken ct)
    {
        IAcpAgent? next = null;
        var old = _agent;
        try
        {
            next = await Relaunch!(ct).ConfigureAwait(false);
            _relaunching = true;
            Detach(old);
            Attach(next);
            _agent = next;
            ReadModels(await LoadOnAsync(next, ct).ConfigureAwait(false));
            // Copilot doesn't keep a selected agent across a load: the next prompt naming one selects it again.
            _selectedAgent = null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (next is not null)
            {
                Detach(next);
                Attach(old);
                _agent = old;
                await next.DisposeAsync().ConfigureAwait(false);
            }

            if (ex is OperationCanceledException)
                throw;
            LogRelaunchFailed(_logger, _fleetSessionId, ex);
            return;
        }
        finally
        {
            _relaunching = false;
        }

        FleetTools = wanted;
        await old.DisposeAsync().ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "GitHub Copilot session {SessionId} couldn't start a process with Fleet's changed tools, so it keeps the one it has")]
    private static partial void LogRelaunchFailed(ILogger logger, string sessionId, Exception ex);

    public async Task SendPromptAsync(string text, PromptOptions? options, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var images = options?.Attachments ?? [];
        if (images.Any(a => !a.Mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)))
            throw new NotSupportedException("GitHub Copilot sessions take images, not other kinds of file.");

        // Not while a compaction Fleet asked for is running: Fleet queues the prompt and sends it when the session idles.
        if (_compacting)
            throw new HarnessBusyException("The session is compacting its context.");

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

        await RefreshFleetToolsAsync(ct).ConfigureAwait(false);
        if (options?.ModelId is { Length: > 0 } wanted && ModelId(wanted) is { } model && model != _model)
        {
            await _agent.Client.RequestAsync("session/set_model", new JsonObject { ["sessionId"] = ResumeToken, ["modelId"] = model }, ct).ConfigureAwait(false);
            _model = model;
        }

        if (options?.Agent is { Length: > 0 } agent && agent != (_selectedAgent ?? CopilotAgent))
        {
            try
            {
                await _agent.Client.RequestAsync("session/set_config_option",
                    new JsonObject { ["sessionId"] = ResumeToken, ["configId"] = "agent", ["value"] = agent == CopilotAgent ? "" : agent }, ct).ConfigureAwait(false);
            }
            catch (AcpException ex)
            {
                // Running on another agent than the one asked for would be worse than not running.
                throw new InvalidOperationException($"GitHub Copilot couldn't switch to the agent \"{agent}\": {ex.Message}", ex);
            }

            _selectedAgent = agent;
        }

        Status = HarnessSessionStatus.Running;
        Emit(_mapper.UserPrompt(text, images, options?.MessageId));
        Emit(_mapper.Status(ActivityStatuses.Busy));
        _ = RunPromptAsync(text, images, PromptMeta(options?.ModelNotes, options?.Skills));
    }

    /// <summary>
    /// The <c>_meta</c> of a <c>session/prompt</c>: Fleet's notes, which weave-copilot puts ahead of the prompt out of the
    /// thread, and the skills it loads ahead of it; null when there is neither.
    /// </summary>
    internal static JsonObject? PromptMeta(IReadOnlyList<string>? notes, IReadOnlyList<string>? skills)
    {
        var meta = new JsonObject();
        if (notes is { Count: > 0 })
            meta["modelNotes"] = new JsonArray([.. notes.Select(n => (JsonNode)JsonValue.Create(n))]);
        if (skills is { Count: > 0 })
            meta["skills"] = new JsonArray([.. skills.Select(name => (JsonNode)JsonValue.Create(name))]);
        return meta.Count > 0 ? meta : null;
    }

    /// <summary>The <c>_meta</c> of a <c>session/new</c> or <c>session/load</c> that points Copilot at Fleet's skills folder; null without one.</summary>
    internal static JsonObject? SkillDirectoriesMeta(string? folder)
        => string.IsNullOrEmpty(folder) ? null : new JsonObject { ["skillDirectories"] = new JsonArray(JsonValue.Create(folder)) };

    private async Task RunPromptAsync(string text, IReadOnlyList<HarnessAttachment> images, JsonObject? meta)
    {
        try
        {
            var blocks = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text });
            var request = new JsonObject { ["sessionId"] = ResumeToken, ["prompt"] = blocks };
            if (meta is not null)
                request["_meta"] = meta;
            foreach (var image in images)
                blocks.Add((JsonNode)new JsonObject { ["type"] = "image", ["data"] = image.Data, ["mimeType"] = image.Mime, ["uri"] = image.Filename });
            var result = await _agent.Client.RequestAsync("session/prompt", request, CancellationToken.None).ConfigureAwait(false);
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
    /// Asks weave-copilot to compact the session (<c>_fleet/compact</c>), which answers when Copilot is done. The session shows
    /// as working meanwhile, so a prompt typed in the meantime waits in Fleet's queue and goes when the session idles after.
    /// </summary>
    public async Task CompactAsync(CompactOptions options, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Status == HarnessSessionStatus.Running || _compacting)
            throw new HarnessBusyException("Wait for the agent to finish its turn, then compact.");

        _compacting = true;
        Emit(_mapper.Status(ActivityStatuses.Busy));
        try
        {
            await _agent.Client.RequestAsync("_fleet/compact", new JsonObject { ["sessionId"] = ResumeToken }, ct).ConfigureAwait(false);
        }
        catch (AcpException ex) when (ex.Code == NoTurnRunning)
        {
            throw new HarnessBusyException("A turn is running; compact the session when it's over.");
        }
        finally
        {
            _compacting = false;
            if (Status != HarnessSessionStatus.Running)
                Emit(_mapper.Status(ActivityStatuses.Idle));
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
            Title = _mapper.CommandOf(call, AcpMapper.Str(call, "toolCallId")) ?? AcpMapper.Str(call, "title"),
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
        if (_relaunching)
            return;
        IReadOnlyList<HarnessEvent> events = method switch
        {
            "session/update" when parameters.TryGetProperty("update", out var update) => _mapper.Update(update),
            "_fleet/fusion" => _mapper.Fusion(parameters),
            "_fleet/compaction" => _mapper.Compaction(parameters),
            _ => [],
        };
        if (!_replaying)
            Emit(events);
    }

    private void OnClosed()
    {
        if (_relaunching)
            return;
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

    /// <summary>Copilot's own agent, and the name Fleet gives it: the session runs on it when no custom agent is selected.</summary>
    internal const string CopilotAgent = "copilot";

    /// <summary>The agents of the session's folder (<c>_fleet/agents</c>), Copilot's own first.</summary>
    public Task<IReadOnlyList<AgentInfo>> GetAgentsAsync(CancellationToken ct) => ListAgentsAsync(_agent.Client, _workingDirectory, ct);

    /// <summary>
    /// <c>_fleet/agents</c> for a folder, with Copilot's own agent first. A weave-copilot too old to know it (-32601) has
    /// no custom agents to offer, so the list is Copilot's own alone.
    /// </summary>
    internal static async Task<IReadOnlyList<AgentInfo>> ListAgentsAsync(AcpClient client, string directory, CancellationToken ct)
    {
        List<AgentInfo> agents = [new() { Name = CopilotAgent, Description = "GitHub Copilot's own agent, without a custom agent", Mode = "primary" }];
        try
        {
            var result = await client.RequestAsync("_fleet/agents", new JsonObject { ["cwd"] = directory }, ct).ConfigureAwait(false);
            if (result.TryGetProperty("agents", out var listed) && listed.ValueKind == JsonValueKind.Array)
            {
                foreach (var agent in listed.EnumerateArray())
                {
                    if (AcpMapper.Str(agent, "name") is { Length: > 0 } name && agents.All(a => a.Name != name))
                        agents.Add(new AgentInfo { Name = name, Description = AcpMapper.Str(agent, "description") ?? AcpMapper.Str(agent, "displayName"), Mode = "primary" });
                }
            }
        }
        catch (AcpException ex) when (ex.Code == MethodNotFound)
        {
        }

        return agents;
    }

    private const int MethodNotFound = -32601;

    /// <summary>The built-in commands of Copilot that Fleet offers: the rest are Fleet's own controls, or need Copilot's terminal.</summary>
    private static readonly HashSet<string> OfferedBuiltIns = new(["init", "review", "security-review", "research", "blame", "env"], StringComparer.Ordinal);

    /// <summary>The commands a session offers (<c>_fleet/commands</c>): its skills and the allow-listed built-ins; -32601 (an older weave-copilot) gives none.</summary>
    public async Task<IReadOnlyList<CommandInfo>> GetCommandsAsync(CancellationToken ct)
    {
        try
        {
            var result = await _agent.Client.RequestAsync("_fleet/commands", new JsonObject { ["sessionId"] = ResumeToken }, ct).ConfigureAwait(false);
            return ParseCommands(result);
        }
        catch (AcpException ex) when (ex.Code == MethodNotFound)
        {
            return [];
        }
    }

    /// <summary>The commands of a <c>_fleet/commands</c> result Fleet shows: skills as skills, allow-listed built-ins as commands.</summary>
    internal static IReadOnlyList<CommandInfo> ParseCommands(JsonElement result)
    {
        List<CommandInfo> commands = [];
        if (!result.TryGetProperty("commands", out var listed) || listed.ValueKind != JsonValueKind.Array)
            return commands;
        foreach (var command in listed.EnumerateArray())
        {
            if (AcpMapper.Str(command, "name") is not { Length: > 0 } name || commands.Any(c => c.Name == name))
                continue;
            var kind = AcpMapper.Str(command, "kind") switch
            {
                "skill" => "skill",
                "builtin" when OfferedBuiltIns.Contains(name) => "command",
                _ => null,
            };
            if (kind is not null)
                commands.Add(new CommandInfo { Name = name, Description = AcpMapper.Str(command, "description"), Kind = kind });
        }

        return commands;
    }

    public Task<string?> AskOffTheRecordAsync(string prompt, CancellationToken ct) => Task.FromResult<string?>(null);

    /// <summary>Sends <c>/name arguments</c> as a prompt: weave-copilot expands it, and the conversation keeps what was typed.</summary>
    public async Task<string?> SendCommandAsync(CommandOptions options, CancellationToken ct)
    {
        await SendPromptAsync(CommandFormatting.FormatCommandPrompt(options),
            new PromptOptions { Agent = options.Agent, ModelId = options.ModelId, MessageId = options.MessageId }, ct).ConfigureAwait(false);
        return options.MessageId;
    }

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
        // A process swap under way finishes first, so the process this ends is the one the session kept.
        var gated = await _relaunch.WaitAsync(RelaunchWait).ConfigureAwait(false);
        End();
        foreach (var id in _asks.Keys)
            Settle(id, PermissionReplies.Gone, null);
        _events.Writer.TryComplete();
        await _agent.DisposeAsync().ConfigureAwait(false);
        if (gated)
            _relaunch.Release();
    }
}

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using WeaveFleet.Application.Canvases;
using WeaveFleet.Application.FleetTools;
using WeaveFleet.Application.Sessions;

namespace WeaveFleet.Infrastructure.Harnesses.ClaudeCode;

/// <summary>
/// The bridge tokens of the claude processes Fleet runs. Each process gets its own, and with it <c>FLEET_URL</c>
/// (<c>{fleet}/agent/{token}</c>), so the agent's calls to Fleet's API say which process, and so which Fleet session,
/// they came from (<see cref="SessionMessages.AgentPathPrefix"/>). A token stops working when its process ends: one that
/// replaces it (to resume on another model or permission mode) gets a new one.
/// </summary>
internal sealed class ClaudeCodeBridgeTokenRegistry
{
    private readonly ConcurrentDictionary<string, HarnessCanvasCaller> _tokens = new(StringComparer.Ordinal);

    /// <summary>A new token for a process running Fleet session <paramref name="fleetSessionId"/>.</summary>
    public string Issue(string fleetSessionId, string ownerUserId)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _tokens[token] = new HarnessCanvasCaller(fleetSessionId, ownerUserId);
        return token;
    }

    /// <summary>The process with <paramref name="token"/> ended; its calls aren't Fleet's to answer any more.</summary>
    public void Revoke(string token) => _tokens.TryRemove(token, out _);

    /// <summary>The Fleet session whose process has <paramref name="token"/>, or null when no running process has it.</summary>
    public HarnessCanvasCaller? Find(string token)
        => !string.IsNullOrWhiteSpace(token) && _tokens.TryGetValue(token, out var caller) ? caller : null;

    /// <summary>A call whose result came and went without Fleet's MCP server hearing of it mustn't be kept forever.</summary>
    internal const int MaxCallsKept = 512;

    private readonly ConcurrentDictionary<string, string> _calls = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _callOrder = new();

    /// <summary>
    /// Claude Code wrote tool call <paramref name="callId"/> on its output: the agent's own call (no
    /// <paramref name="parentCallId"/>), or a subagent's, under the call that started it. Its MCP call to Fleet comes next.
    /// </summary>
    public void NoteCall(string callId, string? parentCallId)
    {
        if (!_calls.TryAdd(callId, parentCallId ?? string.Empty))
            return;
        _callOrder.Enqueue(callId);
        while (_callOrder.Count > MaxCallsKept && _callOrder.TryDequeue(out var oldest))
            _calls.TryRemove(oldest, out _);
    }

    /// <summary>
    /// The subagent call <paramref name="callId"/> was made under: empty for the agent's own call, null while Fleet hasn't
    /// read the call from Claude Code's output yet.
    /// </summary>
    public string? ParentOf(string callId) => _calls.TryGetValue(callId, out var parent) ? parent : null;
}

/// <summary>The bridge tokens of the claude processes Fleet is running.</summary>
internal sealed class ClaudeCodeBridgeTokens(ClaudeCodeBridgeTokenRegistry registry) : IHarnessBridgeTokens
{
    public bool IsKnown(string bridgeToken) => registry.Find(bridgeToken) is not null;
}

/// <summary>
/// Resolves calls from claude processes. One process runs one Fleet session, so the bridge token alone says whose call
/// it is; a subagent runs in its parent's process, so its calls are its parent's session's, the one the user is
/// looking at. The harness session id the call names is the Fleet session's id (<c>FLEET_HARNESS_SESSION_ID</c>) or empty
/// for the agent's own calls; a subagent's calls to Fleet's MCP server name the call that started the subagent
/// (<see cref="ClaudeCodeMcpCalls"/>), and come back marked <see cref="HarnessCanvasCaller.ViaParent"/>.
/// </summary>
internal sealed class ClaudeCodeCanvasCallerResolver(ClaudeCodeBridgeTokenRegistry registry) : IHarnessCanvasCallerResolver
{
    public Task<HarnessCanvasCaller?> ResolveAsync(string bridgeToken, string harnessSessionId, CancellationToken ct = default)
    {
        if (registry.Find(bridgeToken) is not { } caller)
            return Task.FromResult<HarnessCanvasCaller?>(null);
        var own = string.IsNullOrEmpty(harnessSessionId) || string.Equals(harnessSessionId, caller.FleetSessionId, StringComparison.Ordinal);
        return Task.FromResult<HarnessCanvasCaller?>(own ? caller : caller with { ViaParent = true });
    }
}

/// <summary>
/// Places a claude process's calls to Fleet's MCP server. Claude Code names each call by its tool call id in
/// <c>_meta["claudecode/toolUseId"]</c>, the id the call has on Claude Code's output, which says whether the agent or one
/// of its subagents made it. Claude Code writes the call on its output before it runs it, so Fleet has usually read it
/// by the time the MCP call arrives; if not, it waits a moment for it.
/// </summary>
/// <param name="callWait">How long Fleet waits to read a call from Claude Code's output before taking it as the agent's own.</param>
internal sealed class ClaudeCodeMcpCalls(ClaudeCodeBridgeTokenRegistry registry, TimeSpan? callWait = null) : IHarnessMcpCalls
{
    internal const string CallIdKey = "claudecode/toolUseId";

    private readonly TimeSpan _callWait = callWait ?? TimeSpan.FromSeconds(2);

    public async Task<HarnessMcpCall?> PlaceAsync(string bridgeToken, JsonElement meta, CancellationToken ct = default)
    {
        if (registry.Find(bridgeToken) is not { } caller)
            return null;
        if (meta.ValueKind != JsonValueKind.Object || !meta.TryGetProperty(CallIdKey, out var id)
            || id.ValueKind != JsonValueKind.String || id.GetString() is not { Length: > 0 } callId)
            return new HarnessMcpCall(caller.FleetSessionId, null);

        var parent = registry.ParentOf(callId);
        for (var waited = TimeSpan.Zero; parent is null && waited < _callWait; waited += TimeSpan.FromMilliseconds(20))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), ct).ConfigureAwait(false);
            parent = registry.ParentOf(callId);
        }

        return new HarnessMcpCall(string.IsNullOrEmpty(parent) ? caller.FleetSessionId : parent, callId);
    }
}

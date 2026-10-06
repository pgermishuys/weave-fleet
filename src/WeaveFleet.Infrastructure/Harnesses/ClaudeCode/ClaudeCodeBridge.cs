using System.Collections.Concurrent;
using System.Security.Cryptography;
using WeaveFleet.Application.Canvases;
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
}

/// <summary>The bridge tokens of the claude processes Fleet is running.</summary>
internal sealed class ClaudeCodeBridgeTokens(ClaudeCodeBridgeTokenRegistry registry) : IHarnessBridgeTokens
{
    public bool IsKnown(string bridgeToken) => registry.Find(bridgeToken) is not null;
}

/// <summary>
/// Resolves calls from claude processes. One process runs one Fleet session, so the bridge token alone says whose call
/// it is; a subagent runs in its parent's process, so its calls are its parent's session's, the one the user is
/// looking at. The harness session id the call names (<c>FLEET_HARNESS_SESSION_ID</c>, which Fleet sets to the Fleet
/// session's id) isn't needed to tell, and may be empty.
/// </summary>
internal sealed class ClaudeCodeCanvasCallerResolver(ClaudeCodeBridgeTokenRegistry registry) : IHarnessCanvasCallerResolver
{
    public Task<HarnessCanvasCaller?> ResolveAsync(string bridgeToken, string harnessSessionId, CancellationToken ct = default)
        => Task.FromResult(registry.Find(bridgeToken));
}

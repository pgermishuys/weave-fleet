using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using WeaveFleet.Application.Canvases;
using WeaveFleet.Application.FleetTools;
using WeaveFleet.Application.Sessions;

namespace WeaveFleet.Infrastructure.Harnesses.Copilot;

/// <summary>
/// How a GitHub Copilot session gets Fleet's own tools: Fleet's MCP server (<see cref="FleetToolCatalog"/>) at <c>/mcp</c>,
/// named in <c>mcpServers</c> on <c>session/new</c> and <c>session/load</c>. The Copilot CLI over ACP refuses stdio servers
/// but takes HTTP ones with headers, so the session's bridge token goes in <c>Authorization</c>, not in the URL or the
/// agent's environment. weave-copilot passes the server on and never logs it.
/// </summary>
internal sealed class CopilotFleetTools
{
    internal const string ServerName = "fleet";
    private readonly ConcurrentDictionary<string, HarnessCanvasCaller> _tokens = new(StringComparer.Ordinal);

    /// <summary>A token for the process running Fleet session <paramref name="fleetSessionId"/>; it works until <see cref="Revoke"/>.</summary>
    public string Issue(string fleetSessionId, string ownerUserId)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _tokens[token] = new HarnessCanvasCaller(fleetSessionId, ownerUserId);
        return token;
    }

    public void Revoke(string token) => _tokens.TryRemove(token, out _);

    public HarnessCanvasCaller? Find(string token)
        => !string.IsNullOrWhiteSpace(token) && _tokens.TryGetValue(token, out var caller) ? caller : null;

    /// <summary>The <c>mcpServers</c> of a <c>session/new</c> or <c>session/load</c>: Fleet's server, or none while Fleet doesn't know its own address.</summary>
    public static JsonArray McpServers(string? fleetUrl, string bridgeToken) => string.IsNullOrEmpty(fleetUrl)
        ? []
        :
        [
            new JsonObject
            {
                ["type"] = "http",
                ["name"] = ServerName,
                ["url"] = $"{fleetUrl.TrimEnd('/')}/mcp",
                ["headers"] = new JsonArray(new JsonObject { ["name"] = "Authorization", ["value"] = $"Bearer {bridgeToken}" }),
            },
        ];
}

internal sealed class CopilotBridgeTokens(CopilotFleetTools tools) : IHarnessBridgeTokens
{
    public bool IsKnown(string bridgeToken) => tools.Find(bridgeToken) is not null;
}

/// <summary>One weave-copilot process runs one Fleet session, so its token alone says whose call it is.</summary>
internal sealed class CopilotCanvasCallerResolver(CopilotFleetTools tools) : IHarnessCanvasCallerResolver
{
    public Task<HarnessCanvasCaller?> ResolveAsync(string bridgeToken, string harnessSessionId, CancellationToken ct = default)
        => Task.FromResult(tools.Find(bridgeToken));
}

internal sealed class CopilotMcpCalls(CopilotFleetTools tools) : IHarnessMcpCalls
{
    public Task<HarnessMcpCall?> PlaceAsync(string bridgeToken, JsonElement meta, CancellationToken ct = default)
        => Task.FromResult(tools.Find(bridgeToken) is { } caller ? new HarnessMcpCall(caller.FleetSessionId, null) : null);
}

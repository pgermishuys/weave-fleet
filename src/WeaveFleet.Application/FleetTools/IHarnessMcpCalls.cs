using System.Text.Json;

namespace WeaveFleet.Application.FleetTools;

/// <summary>A tool call on Fleet's MCP server, placed by the harness whose process made it.</summary>
/// <param name="HarnessSessionId">
/// The harness's id for the session that made the call, which its <see cref="Canvases.IHarnessCanvasCallerResolver"/>
/// turns into a Fleet session, as it does the <c>harnessSessionId</c> the plugins send.
/// </param>
/// <param name="CallId">The harness's id for the call (the one its tool part has), or null when its client didn't say.</param>
public sealed record HarnessMcpCall(string HarnessSessionId, string? CallId);

/// <summary>
/// How a harness's MCP client names a call to Fleet's MCP server. An MCP request carries no session of its own, so each
/// harness reads its own client's <c>tools/call</c> (what it puts in <c>_meta</c>) for the processes it started.
/// </summary>
public interface IHarnessMcpCalls
{
    /// <summary>The call, or null when <paramref name="bridgeToken"/> isn't one of this harness's processes.</summary>
    /// <param name="meta">The request's <c>params._meta</c>; <see cref="JsonValueKind.Undefined"/> when it sent none.</param>
    Task<HarnessMcpCall?> PlaceAsync(string bridgeToken, JsonElement meta, CancellationToken ct = default);
}

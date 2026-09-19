using System.Text.Json;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Canvases;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode2;

/// <summary>
/// Resolves Fleet tool calls (canvases, the app runner, the browser, messages) from OpenCode 2 servers. The bridge
/// token picks the server, and the V2 session must be attached to that same server. A subagent calls from its own
/// child session, which no Fleet session is attached to, so the resolver follows <c>parentID</c> up to
/// <see cref="MaxParentHops"/> times until it reaches an attached session: the canvas then belongs to the session the
/// user is looking at.
/// </summary>
internal sealed partial class OpenCode2CanvasCallerResolver : IHarnessCanvasCallerResolver
{
    internal const int MaxParentHops = 3;

    private readonly Func<string, OpenCode2Server?> _findServer;
    private readonly ILogger _logger;

    public OpenCode2CanvasCallerResolver(OpenCode2HarnessRuntime runtime, ILogger<OpenCode2CanvasCallerResolver> logger)
        : this(runtime.FindServer, logger)
    {
    }

    /// <param name="findServer">The running server with a bridge token, if any.</param>
    internal OpenCode2CanvasCallerResolver(Func<string, OpenCode2Server?> findServer, ILogger logger)
    {
        _findServer = findServer;
        _logger = logger;
    }

    public async Task<HarnessCanvasCaller?> ResolveAsync(string bridgeToken, string harnessSessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(bridgeToken)
            || string.IsNullOrWhiteSpace(harnessSessionId)
            || _findServer(bridgeToken) is not { } server)
        {
            return null;
        }

        var sessionId = harnessSessionId;
        for (var hop = 0; ; hop++)
        {
            if (server.FindSession(sessionId) is { } session)
                return new HarnessCanvasCaller(session.FleetSessionId, session.OwnerUserId);

            if (hop == MaxParentHops)
                return null;

            try
            {
                // V2 serves every directory from one server, so the session is found without saying where it runs.
                if ((await server.Client.GetSessionAsync(sessionId, ct).ConfigureAwait(false))?.ParentID is not { Length: > 0 } parentId)
                    return null;
                sessionId = parentId;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException
                                       || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                LogParentLookupFailed(_logger, sessionId, ex);
                return null;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not look up the parent of OpenCode 2 session {HarnessSessionId} for a Fleet tool call")]
    private static partial void LogParentLookupFailed(ILogger logger, string harnessSessionId, Exception exception);
}

namespace WeaveFleet.Application.Canvases;

/// <summary>The Fleet session a harness's canvas tool call belongs to, and the user who owns it.</summary>
public sealed record HarnessCanvasCaller(string FleetSessionId, string UserId);

/// <summary>
/// Works out which Fleet session a canvas tool call from a harness process is for. The process proves who it
/// is with the bridge token Fleet gave it, and names the session with the harness's own session id.
/// </summary>
public interface IHarnessCanvasCallerResolver
{
    /// <summary>
    /// Returns the caller, or <c>null</c> when the token is unknown, the session is unknown, or the session
    /// isn't bound to the process that owns the token. Callers mustn't tell these apart.
    /// </summary>
    Task<HarnessCanvasCaller?> ResolveAsync(string bridgeToken, string harnessSessionId, CancellationToken ct = default);
}

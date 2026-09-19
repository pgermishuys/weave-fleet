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

/// <summary>Asks every harness's resolver: each harness knows only the bridge tokens of the processes it started.</summary>
public static class HarnessCanvasCallerResolvers
{
    /// <summary>
    /// The first resolver's caller, or <c>null</c> when none of them places the call. Bridge tokens are unique per
    /// process, so at most one harness knows the token.
    /// </summary>
    public static async Task<HarnessCanvasCaller?> ResolveAsync(
        this IEnumerable<IHarnessCanvasCallerResolver> resolvers,
        string bridgeToken,
        string harnessSessionId,
        CancellationToken ct = default)
    {
        foreach (var resolver in resolvers)
        {
            if (await resolver.ResolveAsync(bridgeToken, harnessSessionId, ct).ConfigureAwait(false) is { } caller)
                return caller;
        }

        return null;
    }
}

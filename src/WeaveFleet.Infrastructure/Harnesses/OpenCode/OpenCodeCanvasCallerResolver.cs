using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Canvases;
using WeaveFleet.Infrastructure.Harnesses.OpenCode.Pooling;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode;

/// <summary>
/// Resolves canvas tool calls from pooled OpenCode processes. The bridge token picks the process, and the
/// OpenCode session must be bound to that same process. A subagent calls from its own child session, which
/// isn't bound, so the resolver follows <c>parentID</c> up to <see cref="MaxParentHops"/> times until it
/// reaches a bound session. The canvas then belongs to the session the user is looking at.
/// </summary>
internal sealed class OpenCodeCanvasCallerResolver : IHarnessCanvasCallerResolver
{
    internal const int MaxParentHops = 3;

    private static readonly Action<ILogger, string, Exception?> LogParentLookupFailed =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1, "ParentLookupFailed"),
            "Could not look up the parent of OpenCode session {OpenCodeSessionId} for a canvas call.");

    private readonly PooledOpenCodeInstanceRegistry _registry;
    private readonly PoolDemuxBindingTable _bindings;
    private readonly ILogger<OpenCodeCanvasCallerResolver> _logger;

    public OpenCodeCanvasCallerResolver(OpenCodeHarnessRuntime runtime, ILogger<OpenCodeCanvasCallerResolver> logger)
        : this(runtime.PooledInstanceRegistry, runtime.PoolBindingTable, logger)
    {
    }

    internal OpenCodeCanvasCallerResolver(
        PooledOpenCodeInstanceRegistry registry,
        PoolDemuxBindingTable bindings,
        ILogger<OpenCodeCanvasCallerResolver> logger)
    {
        _registry = registry;
        _bindings = bindings;
        _logger = logger;
    }

    public async Task<HarnessCanvasCaller?> ResolveAsync(string bridgeToken, string harnessSessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(bridgeToken)
            || string.IsNullOrWhiteSpace(harnessSessionId)
            || !_registry.TryGetInstanceByBridgeToken(bridgeToken, out var instance))
        {
            return null;
        }

        var openCodeSessionId = harnessSessionId;
        for (var hop = 0; ; hop++)
        {
            if (_bindings.TryGetBinding(instance, openCodeSessionId, out var binding))
                return new HarnessCanvasCaller(binding.FleetSessionId, binding.UserId, ViaParent: hop > 0);

            if (hop == MaxParentHops)
                return null;

            var parentId = await GetParentIdAsync(instance, openCodeSessionId, ct).ConfigureAwait(false);
            if (parentId is null)
                return null;

            openCodeSessionId = parentId;
        }
    }

    /// <summary>
    /// Asks the process for a session's parent. OpenCode finds sessions by project, so the lookup is tried in
    /// each workspace that has a session bound to this process: a child lives where its parent does.
    /// </summary>
    private async Task<string?> GetParentIdAsync(PooledOpenCodeInstance instance, string openCodeSessionId, CancellationToken ct)
    {
        if (instance.HttpClient is null)
            return null;

        var directories = _bindings.GetBindingsForInstance(instance)
            .Select(binding => binding.Directory)
            .Distinct(StringComparer.Ordinal);

        foreach (var directory in directories)
        {
            try
            {
                return await instance.HttpClient.GetSessionParentIdAsync(openCodeSessionId, directory, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // Not in this workspace's project; try the next one.
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException
                                       || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                LogParentLookupFailed(_logger, openCodeSessionId, ex);
                return null;
            }
        }

        return null;
    }
}

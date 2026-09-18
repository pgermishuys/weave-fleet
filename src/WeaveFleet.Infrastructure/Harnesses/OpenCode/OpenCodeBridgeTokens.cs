using WeaveFleet.Application.Sessions;
using WeaveFleet.Infrastructure.Harnesses.OpenCode.Pooling;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode;

/// <summary>The bridge tokens of the pooled OpenCode processes that are running now.</summary>
internal sealed class OpenCodeBridgeTokens(OpenCodeHarnessRuntime runtime) : IHarnessBridgeTokens
{
    private readonly PooledOpenCodeInstanceRegistry _registry = runtime.PooledInstanceRegistry;

    public bool IsKnown(string bridgeToken)
        => !string.IsNullOrWhiteSpace(bridgeToken) && _registry.TryGetInstanceByBridgeToken(bridgeToken, out _);
}

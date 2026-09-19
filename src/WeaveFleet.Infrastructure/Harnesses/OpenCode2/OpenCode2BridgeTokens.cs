using WeaveFleet.Application.Sessions;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode2;

/// <summary>The bridge tokens of the OpenCode 2 servers Fleet is running.</summary>
internal sealed class OpenCode2BridgeTokens(OpenCode2HarnessRuntime runtime) : IHarnessBridgeTokens
{
    public bool IsKnown(string bridgeToken)
        => !string.IsNullOrWhiteSpace(bridgeToken) && runtime.IsBridgeToken(bridgeToken);
}

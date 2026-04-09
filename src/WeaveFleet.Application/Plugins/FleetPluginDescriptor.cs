namespace WeaveFleet.Application.Plugins;

public sealed record FleetPluginDescriptor(
    string Id,
    string DisplayName,
    PluginTrustLevel TrustLevel,
    bool HasFrontend,
    bool HasBackend);

public enum PluginTrustLevel
{
    BuiltIn = 0
}

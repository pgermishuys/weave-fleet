namespace WeaveFleet.Application.Plugins;

public sealed record PluginStatus(
    string PluginId,
    PluginConnectionStatus Status,
    DateTimeOffset? ConnectedAt,
    IReadOnlyList<PluginActionDescriptor> Actions);

public sealed record PluginActionDescriptor(
    string Id,
    string Label,
    string? Href,
    string? Method);

public enum PluginConnectionStatus
{
    Disconnected = 0,
    Connected = 1,
    Error = 2
}

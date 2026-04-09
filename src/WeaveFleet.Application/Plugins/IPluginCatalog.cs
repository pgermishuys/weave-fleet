namespace WeaveFleet.Application.Plugins;

public interface IPluginCatalog
{
    Task<IReadOnlyList<FleetPluginDescriptor>> GetDescriptorsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<PluginStatus>> GetStatusesAsync(CancellationToken cancellationToken);
}

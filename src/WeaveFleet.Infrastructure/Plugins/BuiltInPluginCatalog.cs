using WeaveFleet.Application.Plugins;

namespace WeaveFleet.Infrastructure.Plugins;

public sealed class BuiltInPluginCatalog(IEnumerable<IBackendPlugin> backendPlugins) : IPluginCatalog
{
    private readonly IBackendPlugin[] _backendPlugins = backendPlugins
        .OrderBy(plugin => plugin.Descriptor.DisplayName, StringComparer.Ordinal)
        .ToArray();

    public Task<IReadOnlyList<FleetPluginDescriptor>> GetDescriptorsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<FleetPluginDescriptor> descriptors = _backendPlugins
            .Select(plugin => plugin.Descriptor)
            .ToArray();

        return Task.FromResult(descriptors);
    }

    public async Task<IReadOnlyList<PluginStatus>> GetStatusesAsync(CancellationToken cancellationToken)
    {
        var statuses = new List<PluginStatus>(_backendPlugins.Length);

        foreach (var plugin in _backendPlugins)
        {
            statuses.Add(await plugin.GetStatusAsync(cancellationToken).ConfigureAwait(false));
        }

        return statuses;
    }
}

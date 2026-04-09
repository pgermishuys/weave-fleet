using System.Text.Json.Nodes;
using WeaveFleet.Application.Plugins;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Infrastructure.Plugins;

public sealed class PluginStateStore(IIntegrationStore integrationStore) : IPluginStateStore
{
    public Task<JsonObject?> GetStateAsync(string pluginId, CancellationToken cancellationToken)
        => integrationStore.GetConfigAsync(pluginId, cancellationToken);

    public Task SetStateAsync(string pluginId, JsonObject state, CancellationToken cancellationToken)
        => integrationStore.SetConfigAsync(pluginId, state, cancellationToken);

    public Task RemoveStateAsync(string pluginId, CancellationToken cancellationToken)
        => integrationStore.RemoveConfigAsync(pluginId, cancellationToken);

    public Task<IReadOnlyDictionary<string, JsonObject>> GetAllStatesAsync(CancellationToken cancellationToken)
        => integrationStore.GetAllConfigsAsync(cancellationToken);
}

using System.Text.Json.Nodes;
using WeaveFleet.Application.Plugins;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Infrastructure.Plugins;

public sealed class PluginStateStore(IIntegrationStore integrationStore) : IPluginStateStore
{
    public Task<JsonObject?> GetStateAsync(string pluginId, string userId, CancellationToken cancellationToken)
        => integrationStore.GetConfigAsync(pluginId, userId, cancellationToken);

    public Task SetStateAsync(string pluginId, string userId, JsonObject state, CancellationToken cancellationToken)
        => integrationStore.SetConfigAsync(pluginId, userId, state, cancellationToken);

    public Task RemoveStateAsync(string pluginId, string userId, CancellationToken cancellationToken)
        => integrationStore.RemoveConfigAsync(pluginId, userId, cancellationToken);

    public Task<IReadOnlyDictionary<string, JsonObject>> GetAllStatesAsync(string userId, CancellationToken cancellationToken)
        => integrationStore.GetAllConfigsAsync(userId, cancellationToken);
}

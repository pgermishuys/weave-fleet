using System.Text.Json.Nodes;

namespace WeaveFleet.Application.Plugins;

public interface IPluginStateStore
{
    Task<JsonObject?> GetStateAsync(string pluginId, string userId, CancellationToken cancellationToken);

    Task SetStateAsync(string pluginId, string userId, JsonObject state, CancellationToken cancellationToken);

    Task RemoveStateAsync(string pluginId, string userId, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, JsonObject>> GetAllStatesAsync(string userId, CancellationToken cancellationToken);
}

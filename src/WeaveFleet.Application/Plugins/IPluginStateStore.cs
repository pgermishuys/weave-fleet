using System.Text.Json.Nodes;

namespace WeaveFleet.Application.Plugins;

public interface IPluginStateStore
{
    Task<JsonObject?> GetStateAsync(string pluginId, CancellationToken cancellationToken);

    Task SetStateAsync(string pluginId, JsonObject state, CancellationToken cancellationToken);

    Task RemoveStateAsync(string pluginId, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, JsonObject>> GetAllStatesAsync(CancellationToken cancellationToken);
}

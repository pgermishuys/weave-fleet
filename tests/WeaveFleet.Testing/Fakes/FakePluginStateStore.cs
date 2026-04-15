using System.Text.Json.Nodes;
using WeaveFleet.Application.Plugins;

namespace WeaveFleet.Testing.Fakes;

public sealed class FakePluginStateStore : IPluginStateStore
{
    private readonly Dictionary<(string PluginId, string UserId), JsonObject> _store = new();

    public Task<JsonObject?> GetStateAsync(string pluginId, string userId, CancellationToken cancellationToken)
    {
        _store.TryGetValue((pluginId, userId), out var value);
        return Task.FromResult(value);
    }

    public Task SetStateAsync(string pluginId, string userId, JsonObject state, CancellationToken cancellationToken)
    {
        _store[(pluginId, userId)] = state;
        return Task.CompletedTask;
    }

    public Task RemoveStateAsync(string pluginId, string userId, CancellationToken cancellationToken)
    {
        _store.Remove((pluginId, userId));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, JsonObject>> GetAllStatesAsync(string userId, CancellationToken cancellationToken)
    {
        var result = _store
            .Where(kvp => kvp.Key.UserId == userId)
            .ToDictionary(kvp => kvp.Key.PluginId, kvp => kvp.Value);

        return Task.FromResult<IReadOnlyDictionary<string, JsonObject>>(result);
    }
}

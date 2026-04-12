using System.Text.Json.Nodes;

namespace WeaveFleet.Application.Services;

/// <summary>
/// Transitional persistence abstraction for integration-shaped config data.
/// The canonical host seam is <c>IPluginStateStore</c>; keep this interface only
/// while compatibility callers are being migrated.
/// </summary>
public interface IIntegrationStore
{
    Task<JsonObject?> GetConfigAsync(string id, string userId, CancellationToken ct = default);
    Task SetConfigAsync(string id, string userId, JsonObject config, CancellationToken ct = default);
    Task RemoveConfigAsync(string id, string userId, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, JsonObject>> GetAllConfigsAsync(string userId, CancellationToken ct = default);
}

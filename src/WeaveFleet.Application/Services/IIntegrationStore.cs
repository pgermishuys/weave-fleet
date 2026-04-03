using System.Text.Json;
using System.Text.Json.Nodes;

namespace WeaveFleet.Application.Services;

/// <summary>
/// Abstraction for persisting integration tokens and settings (e.g. GitHub OAuth token).
/// </summary>
public interface IIntegrationStore
{
    Task<JsonObject?> GetConfigAsync(string id, CancellationToken ct = default);
    Task SetConfigAsync(string id, JsonObject config, CancellationToken ct = default);
    Task RemoveConfigAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, JsonObject>> GetAllConfigsAsync(CancellationToken ct = default);
}

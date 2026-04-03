using System.Text.Json;
using System.Text.Json.Nodes;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Infrastructure.Services;

/// <summary>
/// Persists integration configs in ~/.weave/integrations.json.
/// </summary>
public sealed class FileIntegrationStore : IIntegrationStore
{
    private static readonly string StorePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".weave", "integrations.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private static readonly SemaphoreSlim FileLock = new(1, 1);

    public async Task<JsonObject?> GetConfigAsync(string id, CancellationToken ct = default)
    {
        var all = await ReadAllAsync(ct).ConfigureAwait(false);
        return all.TryGetPropertyValue(id, out var value) && value is JsonObject obj ? obj : null;
    }

    public async Task SetConfigAsync(string id, JsonObject config, CancellationToken ct = default)
    {
        await FileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var all = await ReadAllAsync(ct).ConfigureAwait(false);
            all[id] = config.DeepClone();
            await WriteAllAsync(all, ct).ConfigureAwait(false);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task RemoveConfigAsync(string id, CancellationToken ct = default)
    {
        await FileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var all = await ReadAllAsync(ct).ConfigureAwait(false);
            all.Remove(id);
            await WriteAllAsync(all, ct).ConfigureAwait(false);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<IReadOnlyDictionary<string, JsonObject>> GetAllConfigsAsync(CancellationToken ct = default)
    {
        var all = await ReadAllAsync(ct).ConfigureAwait(false);
        var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var (key, value) in all)
        {
            if (value is JsonObject obj)
                result[key] = obj;
        }
        return result;
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private static async Task<JsonObject> ReadAllAsync(CancellationToken ct)
    {
        if (!File.Exists(StorePath))
            return [];

        var json = await File.ReadAllTextAsync(StorePath, ct).ConfigureAwait(false);
        try
        {
            return JsonNode.Parse(json) as JsonObject ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static async Task WriteAllAsync(JsonObject data, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(StorePath)!;
        Directory.CreateDirectory(dir);
        var json = data.ToJsonString(Options);
        await File.WriteAllTextAsync(StorePath, json, ct).ConfigureAwait(false);
    }
}

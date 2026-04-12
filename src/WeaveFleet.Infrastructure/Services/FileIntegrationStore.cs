using System.Text.Json;
using System.Text.Json.Nodes;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Infrastructure.Services;

/// <summary>
/// Transitional file-backed store for integration-shaped config data.
/// Plugin state should flow through <c>IPluginStateStore</c>, which currently
/// adapts to this implementation during the migration.
/// Storage is user-scoped: each user gets their own file at ~/.weave/integrations/{userId}.json.
/// </summary>
public sealed class FileIntegrationStore : IIntegrationStore
{
    private static readonly string IntegrationsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".weave", "integrations");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private static readonly SemaphoreSlim FileLock = new(1, 1);

    public async Task<JsonObject?> GetConfigAsync(string id, string userId, CancellationToken ct = default)
    {
        ValidateUserId(userId);
        var all = await ReadAllAsync(userId, ct).ConfigureAwait(false);
        return all.TryGetPropertyValue(id, out var value) && value is JsonObject obj ? obj : null;
    }

    public async Task SetConfigAsync(string id, string userId, JsonObject config, CancellationToken ct = default)
    {
        ValidateUserId(userId);
        await FileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var all = await ReadAllAsync(userId, ct).ConfigureAwait(false);
            all[id] = config.DeepClone();
            await WriteAllAsync(userId, all, ct).ConfigureAwait(false);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task RemoveConfigAsync(string id, string userId, CancellationToken ct = default)
    {
        ValidateUserId(userId);
        await FileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var all = await ReadAllAsync(userId, ct).ConfigureAwait(false);
            all.Remove(id);
            await WriteAllAsync(userId, all, ct).ConfigureAwait(false);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<IReadOnlyDictionary<string, JsonObject>> GetAllConfigsAsync(string userId, CancellationToken ct = default)
    {
        ValidateUserId(userId);
        var all = await ReadAllAsync(userId, ct).ConfigureAwait(false);
        var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var (key, value) in all)
        {
            if (value is JsonObject obj)
                result[key] = obj;
        }
        return result;
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Validates that the userId doesn't contain path separators or other dangerous values.
    /// </summary>
    private static void ValidateUserId(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("userId cannot be empty.", nameof(userId));

        if (userId.Contains('/', StringComparison.Ordinal) ||
            userId.Contains('\\', StringComparison.Ordinal) ||
            userId.Contains('\0', StringComparison.Ordinal) ||
            userId is "." or "..")
        {
            throw new ArgumentException($"userId contains invalid characters: '{userId}'", nameof(userId));
        }
    }

    private static string GetStorePath(string userId) =>
        Path.Combine(IntegrationsDir, $"{userId}.json");

    private static async Task<JsonObject> ReadAllAsync(string userId, CancellationToken ct)
    {
        var storePath = GetStorePath(userId);
        if (!File.Exists(storePath))
            return [];

        var json = await File.ReadAllTextAsync(storePath, ct).ConfigureAwait(false);
        try
        {
            return JsonNode.Parse(json) as JsonObject ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static async Task WriteAllAsync(string userId, JsonObject data, CancellationToken ct)
    {
        var storePath = GetStorePath(userId);
        var dir = Path.GetDirectoryName(storePath)!;
        Directory.CreateDirectory(dir);
        var json = data.ToJsonString(Options);
        await File.WriteAllTextAsync(storePath, json, ct).ConfigureAwait(false);
    }
}

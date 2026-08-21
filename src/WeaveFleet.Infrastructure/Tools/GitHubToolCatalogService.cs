using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Tools;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Skills;
using WeaveFleet.Domain.Tools;

namespace WeaveFleet.Infrastructure.Tools;

/// <summary>
/// Fetches the tool catalog from a GitHub raw URL and caches it locally.
/// </summary>
public sealed partial class GitHubToolCatalogService : IToolCatalogService
{
    private const string DefaultCatalogUrl = "https://raw.githubusercontent.com/pgermishuys/weave-fleet/main/catalog/catalog.json";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GitHubToolCatalogService> _logger;
    private readonly string _catalogUrl;
    private readonly string? _baseDirectory;

    public GitHubToolCatalogService(
        IHttpClientFactory httpClientFactory,
        ILogger<GitHubToolCatalogService> logger,
        string? baseDirectory = null)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _catalogUrl = DefaultCatalogUrl;
        _baseDirectory = baseDirectory;
    }

    public async Task<Result<ToolCatalogResponse>> FetchCatalogAsync(CancellationToken cancellationToken = default)
    {
        var cacheFilePath = GetCacheFilePath();

        // Try to fetch from remote
        var remoteResult = await FetchFromRemoteAsync(cancellationToken).ConfigureAwait(false);
        if (remoteResult.IsSuccess)
        {
            await WriteCacheAsync(cacheFilePath, remoteResult.Value, cancellationToken).ConfigureAwait(false);
            return Result.Success(new ToolCatalogResponse(remoteResult.Value, IsStale: false, CachedAt: DateTimeOffset.UtcNow));
        }

        Log.FetchFromRemoteFailed(_logger, remoteResult.Error.Description);

        // Fall back to cache
        var cacheResult = await ReadCacheAsync(cacheFilePath, cancellationToken).ConfigureAwait(false);
        if (cacheResult.IsSuccess)
        {
            var isStale = DateTimeOffset.UtcNow - cacheResult.Value.CachedAt > CacheTtl;
            return Result.Success(new ToolCatalogResponse(cacheResult.Value.Entries, IsStale: isStale, CachedAt: cacheResult.Value.CachedAt));
        }

        // No cache available
        return remoteResult.Error;
    }

    private async Task<Result<IReadOnlyList<ToolCatalogEntry>>> FetchFromRemoteAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);

            var response = await client.GetAsync(_catalogUrl, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new FleetError(
                    "ToolCatalog.FetchFailed",
                    $"GitHub returned HTTP {(int)response.StatusCode} when fetching catalog.");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var parseResult = ParseCatalog(json);
            if (parseResult.IsFailure)
                return parseResult.Error;

            return Result.Success(parseResult.Value);
        }
        catch (HttpRequestException ex)
        {
            return new FleetError("ToolCatalog.NetworkError", $"Network error fetching catalog: {ex.Message}");
        }
        catch (TaskCanceledException ex) when (ex.CancellationToken == cancellationToken)
        {
            return new FleetError("ToolCatalog.Cancelled", "Catalog fetch was cancelled.");
        }
        catch (TaskCanceledException)
        {
            return new FleetError("ToolCatalog.Timeout", "Catalog fetch timed out.");
        }
        catch (Exception ex)
        {
            Log.UnexpectedFetchError(_logger, ex);
            return new FleetError("ToolCatalog.UnexpectedError", $"Unexpected error: {ex.Message}");
        }
    }

    private async Task WriteCacheAsync(string cacheFilePath, IReadOnlyList<ToolCatalogEntry> entries, CancellationToken cancellationToken)
    {
        try
        {
            var cacheDirectory = Path.GetDirectoryName(cacheFilePath);
            if (!string.IsNullOrEmpty(cacheDirectory) && !Directory.Exists(cacheDirectory))
            {
                Directory.CreateDirectory(cacheDirectory);
            }

            var cacheData = new CachedToolCatalog(entries, DateTimeOffset.UtcNow);
            var json = JsonSerializer.Serialize(cacheData, InfrastructureJsonContext.Default.CachedToolCatalog);

            await File.WriteAllTextAsync(cacheFilePath, json, cancellationToken).ConfigureAwait(false);
            Log.CatalogCached(_logger, cacheFilePath);
        }
        catch (Exception ex)
        {
            Log.WriteCacheFailed(_logger, ex, cacheFilePath);
        }
    }

    private async Task<Result<CachedToolCatalog>> ReadCacheAsync(string cacheFilePath, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(cacheFilePath))
            {
                return FleetError.NotFoundFor("ToolCatalogCache", cacheFilePath);
            }

            var json = await File.ReadAllTextAsync(cacheFilePath, cancellationToken).ConfigureAwait(false);
            var cacheData = JsonSerializer.Deserialize(json, InfrastructureJsonContext.Default.CachedToolCatalog);

            if (cacheData is null)
            {
                return FleetError.ValidationError("ToolCatalogCache", "Cache file is empty or invalid.");
            }

            Log.CatalogLoadedFromCache(_logger, cacheData.Entries.Count, cacheData.CachedAt);
            return Result.Success(cacheData);
        }
        catch (Exception ex)
        {
            Log.ReadCacheFailed(_logger, ex, cacheFilePath);
            return FleetError.ValidationError("ToolCatalogCache", $"Failed to read cache: {ex.Message}");
        }
    }

    private static Result<IReadOnlyList<ToolCatalogEntry>> ParseCatalog(string json)
    {
        try
        {
            var root = JsonNode.Parse(json);
            if (root is not JsonObject rootObject)
            {
                return FleetError.ValidationError("ToolCatalog", "Catalog JSON must be an object.");
            }

            if (!rootObject.TryGetPropertyValue("tools", out var toolsNode) || toolsNode is not JsonArray toolsArray)
            {
                return FleetError.ValidationError("ToolCatalog", "Catalog JSON must contain a 'tools' array.");
            }

            var entries = new List<ToolCatalogEntry>();

            foreach (var toolNode in toolsArray)
            {
                if (toolNode is not JsonObject toolObject)
                    continue;

                var entryResult = ParseCatalogEntry(toolObject);
                if (entryResult.IsSuccess)
                {
                    entries.Add(entryResult.Value);
                }
            }

            return Result.Success<IReadOnlyList<ToolCatalogEntry>>(entries);
        }
        catch (JsonException ex)
        {
            return FleetError.ValidationError("ToolCatalog", $"Invalid JSON: {ex.Message}");
        }
    }

    private static Result<ToolCatalogEntry> ParseCatalogEntry(JsonObject toolObject)
    {
        var name = toolObject["name"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return FleetError.ValidationError("ToolCatalog", "Catalog entry missing required 'name' field.");
        }

        var toolTypeString = toolObject["toolType"]?.GetValue<string>()?.Trim();
        if (!Enum.TryParse<ToolType>(toolTypeString, ignoreCase: true, out var toolType))
        {
            return FleetError.ValidationError("ToolCatalog", $"Catalog entry '{name}' has invalid 'toolType' value: {toolTypeString}");
        }

        var sourceString = toolObject["source"]?.GetValue<string>()?.Trim();
        if (!Enum.TryParse<SkillSource>(sourceString, ignoreCase: true, out var source))
        {
            return FleetError.ValidationError("ToolCatalog", $"Catalog entry '{name}' has invalid 'source' value: {sourceString}");
        }

        var entry = new ToolCatalogEntry
        {
            Name = name,
            DisplayName = toolObject["displayName"]?.GetValue<string>()?.Trim(),
            Description = toolObject["description"]?.GetValue<string>()?.Trim(),
            ToolType = toolType,
            Source = source,
            Command = toolObject["command"]?.GetValue<string>()?.Trim(),
            Args = ParseStringArray(toolObject["args"]),
            Env = ParseStringDictionary(toolObject["env"]),
            RepoUrl = toolObject["repoUrl"]?.GetValue<string>()?.Trim(),
            Ref = toolObject["ref"]?.GetValue<string>()?.Trim(),
            SubPath = toolObject["subPath"]?.GetValue<string>()?.Trim(),
            LocalPath = toolObject["localPath"]?.GetValue<string>()?.Trim(),
            Author = toolObject["author"]?.GetValue<string>()?.Trim(),
            Version = toolObject["version"]?.GetValue<string>()?.Trim(),
            Tags = ParseStringArray(toolObject["tags"]) ?? [],
            CreatedAt = ParseDateTimeOffset(toolObject["createdAt"]),
            UpdatedAt = ParseDateTimeOffset(toolObject["updatedAt"])
        };

        return Result.Success(entry);
    }

    private static List<string>? ParseStringArray(JsonNode? node)
    {
        if (node is not JsonArray array)
            return null;

        var result = new List<string>();
        foreach (var item in array)
        {
            var value = item?.GetValue<string>()?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                result.Add(value);
            }
        }

        return result.Count > 0 ? result : null;
    }

    private static Dictionary<string, string>? ParseStringDictionary(JsonNode? node)
    {
        if (node is not JsonObject obj)
            return null;

        var result = new Dictionary<string, string>();
        foreach (var kvp in obj)
        {
            var value = kvp.Value?.GetValue<string>()?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                result[kvp.Key] = value;
            }
        }

        return result.Count > 0 ? result : null;
    }

    private static DateTimeOffset? ParseDateTimeOffset(JsonNode? node)
    {
        if (node is null)
            return null;

        try
        {
            return node.GetValue<DateTimeOffset>();
        }
        catch
        {
            return null;
        }
    }

    private string GetCacheFilePath()
    {
        var baseDir = _baseDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var configDir = Path.Combine(baseDir, ".config", "weave-fleet");
        return Path.Combine(configDir, "tool-catalog-cache.json");
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to fetch tool catalog from remote: {Error}. Attempting to use cached data.")]
        public static partial void FetchFromRemoteFailed(ILogger logger, string error);

        [LoggerMessage(Level = LogLevel.Error, Message = "Unexpected error fetching tool catalog from remote.")]
        public static partial void UnexpectedFetchError(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Information, Message = "Tool catalog cached to {CacheFilePath}")]
        public static partial void CatalogCached(ILogger logger, string cacheFilePath);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to write tool catalog cache to {CacheFilePath}")]
        public static partial void WriteCacheFailed(ILogger logger, Exception ex, string cacheFilePath);

        [LoggerMessage(Level = LogLevel.Information, Message = "Loaded tool catalog from cache ({EntryCount} entries, cached at {CachedAt})")]
        public static partial void CatalogLoadedFromCache(ILogger logger, int entryCount, DateTimeOffset cachedAt);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to read tool catalog cache from {CacheFilePath}")]
        public static partial void ReadCacheFailed(ILogger logger, Exception ex, string cacheFilePath);
    }
}

/// <summary>Cached tool catalog with timestamp.</summary>
internal sealed record CachedToolCatalog(IReadOnlyList<ToolCatalogEntry> Entries, DateTimeOffset CachedAt);

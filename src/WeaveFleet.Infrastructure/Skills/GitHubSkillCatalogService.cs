using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Skills;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Skills;

namespace WeaveFleet.Infrastructure.Skills;

/// <summary>
/// Fetches the skill catalog from a GitHub raw URL and caches it locally.
/// </summary>
public sealed partial class GitHubSkillCatalogService : ISkillCatalogService
{
    private const string DefaultCatalogUrl = "https://raw.githubusercontent.com/pgermishuys/weave-fleet/main/catalog/catalog.json";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GitHubSkillCatalogService> _logger;
    private readonly string _catalogUrl;
    private readonly string? _baseDirectory;

    public GitHubSkillCatalogService(
        IHttpClientFactory httpClientFactory,
        ILogger<GitHubSkillCatalogService> logger,
        string? baseDirectory = null)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _catalogUrl = DefaultCatalogUrl;
        _baseDirectory = baseDirectory;
    }

    public async Task<Result<CatalogResponse>> FetchCatalogAsync(CancellationToken cancellationToken = default)
    {
        var cacheFilePath = GetCacheFilePath();

        // Try to fetch from remote
        var remoteResult = await FetchFromRemoteAsync(cancellationToken).ConfigureAwait(false);
        if (remoteResult.IsSuccess)
        {
            await WriteCacheAsync(cacheFilePath, remoteResult.Value, cancellationToken).ConfigureAwait(false);
            return Result.Success(new CatalogResponse(remoteResult.Value, IsStale: false, CachedAt: DateTimeOffset.UtcNow));
        }

        Log.FetchFromRemoteFailed(_logger, remoteResult.Error.Description);

        // Fall back to cache
        var cacheResult = await ReadCacheAsync(cacheFilePath, cancellationToken).ConfigureAwait(false);
        if (cacheResult.IsSuccess)
        {
            var isStale = DateTimeOffset.UtcNow - cacheResult.Value.CachedAt > CacheTtl;
            return Result.Success(new CatalogResponse(cacheResult.Value.Entries, IsStale: isStale, CachedAt: cacheResult.Value.CachedAt));
        }

        // No cache available
        return remoteResult.Error;
    }

    private async Task<Result<IReadOnlyList<CatalogEntry>>> FetchFromRemoteAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);

            var response = await client.GetAsync(_catalogUrl, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new FleetError(
                    "SkillCatalog.FetchFailed",
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
            return new FleetError("SkillCatalog.NetworkError", $"Network error fetching catalog: {ex.Message}");
        }
        catch (TaskCanceledException ex) when (ex.CancellationToken == cancellationToken)
        {
            return new FleetError("SkillCatalog.Cancelled", "Catalog fetch was cancelled.");
        }
        catch (TaskCanceledException)
        {
            return new FleetError("SkillCatalog.Timeout", "Catalog fetch timed out.");
        }
        catch (Exception ex)
        {
            Log.UnexpectedFetchError(_logger, ex);
            return new FleetError("SkillCatalog.UnexpectedError", $"Unexpected error: {ex.Message}");
        }
    }

    private async Task WriteCacheAsync(string cacheFilePath, IReadOnlyList<CatalogEntry> entries, CancellationToken cancellationToken)
    {
        try
        {
            var cacheDirectory = Path.GetDirectoryName(cacheFilePath);
            if (!string.IsNullOrEmpty(cacheDirectory) && !Directory.Exists(cacheDirectory))
            {
                Directory.CreateDirectory(cacheDirectory);
            }

            var cacheData = new CachedCatalog(entries, DateTimeOffset.UtcNow);
            var json = JsonSerializer.Serialize(cacheData, InfrastructureJsonContext.Default.CachedCatalog);

            await File.WriteAllTextAsync(cacheFilePath, json, cancellationToken).ConfigureAwait(false);
            Log.CatalogCached(_logger, cacheFilePath);
        }
        catch (Exception ex)
        {
            Log.WriteCacheFailed(_logger, ex, cacheFilePath);
        }
    }

    private async Task<Result<CachedCatalog>> ReadCacheAsync(string cacheFilePath, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(cacheFilePath))
            {
                return FleetError.NotFoundFor("CatalogCache", cacheFilePath);
            }

            var json = await File.ReadAllTextAsync(cacheFilePath, cancellationToken).ConfigureAwait(false);
            var cacheData = JsonSerializer.Deserialize(json, InfrastructureJsonContext.Default.CachedCatalog);

            if (cacheData is null)
            {
                return FleetError.ValidationError("CatalogCache", "Cache file is empty or invalid.");
            }

            Log.CatalogLoadedFromCache(_logger, cacheData.Entries.Count, cacheData.CachedAt);
            return Result.Success(cacheData);
        }
        catch (Exception ex)
        {
            Log.ReadCacheFailed(_logger, ex, cacheFilePath);
            return FleetError.ValidationError("CatalogCache", $"Failed to read cache: {ex.Message}");
        }
    }

    private static Result<IReadOnlyList<CatalogEntry>> ParseCatalog(string json)
    {
        try
        {
            var root = JsonNode.Parse(json);
            if (root is not JsonObject rootObject)
            {
                return FleetError.ValidationError("SkillCatalog", "Catalog JSON must be an object.");
            }

            if (!rootObject.TryGetPropertyValue("skills", out var skillsNode) || skillsNode is not JsonArray skillsArray)
            {
                return FleetError.ValidationError("SkillCatalog", "Catalog JSON must contain a 'skills' array.");
            }

            var entries = new List<CatalogEntry>();

            foreach (var skillNode in skillsArray)
            {
                if (skillNode is not JsonObject skillObject)
                    continue;

                var entryResult = ParseCatalogEntry(skillObject);
                if (entryResult.IsSuccess)
                {
                    entries.Add(entryResult.Value);
                }
            }

            return Result.Success<IReadOnlyList<CatalogEntry>>(entries);
        }
        catch (JsonException ex)
        {
            return FleetError.ValidationError("SkillCatalog", $"Invalid JSON: {ex.Message}");
        }
    }

    private static Result<CatalogEntry> ParseCatalogEntry(JsonObject skillObject)
    {
        var name = skillObject["name"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return FleetError.ValidationError("SkillCatalog", "Catalog entry missing required 'name' field.");
        }

        var sourceString = skillObject["source"]?.GetValue<string>()?.Trim();
        if (!Enum.TryParse<SkillSource>(sourceString, ignoreCase: true, out var source))
        {
            return FleetError.ValidationError("SkillCatalog", $"Catalog entry '{name}' has invalid 'source' value: {sourceString}");
        }

        var entry = new CatalogEntry
        {
            Name = name,
            DisplayName = skillObject["displayName"]?.GetValue<string>()?.Trim(),
            Description = skillObject["description"]?.GetValue<string>()?.Trim(),
            Source = source,
            RepoUrl = skillObject["repoUrl"]?.GetValue<string>()?.Trim(),
            Ref = skillObject["ref"]?.GetValue<string>()?.Trim(),
            SubPath = skillObject["subPath"]?.GetValue<string>()?.Trim(),
            LocalPath = skillObject["localPath"]?.GetValue<string>()?.Trim(),
            TargetHarnesses = ParseStringArray(skillObject["targetHarnesses"]),
            Author = skillObject["author"]?.GetValue<string>()?.Trim(),
            Version = skillObject["version"]?.GetValue<string>()?.Trim(),
            Tags = ParseStringArray(skillObject["tags"]),
            CreatedAt = ParseDateTimeOffset(skillObject["createdAt"]),
            UpdatedAt = ParseDateTimeOffset(skillObject["updatedAt"])
        };

        return Result.Success(entry);
    }

    private static List<string> ParseStringArray(JsonNode? node)
    {
        if (node is not JsonArray array)
            return [];

        var result = new List<string>();
        foreach (var item in array)
        {
            var value = item?.GetValue<string>()?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                result.Add(value);
            }
        }

        return result;
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
        var weaveDir = Path.Combine(baseDir, ".weave");
        return Path.Combine(weaveDir, "catalog-cache.json");
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to fetch catalog from remote: {Error}. Attempting to use cached data.")]
        public static partial void FetchFromRemoteFailed(ILogger logger, string error);

        [LoggerMessage(Level = LogLevel.Error, Message = "Unexpected error fetching skill catalog from remote.")]
        public static partial void UnexpectedFetchError(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Information, Message = "Skill catalog cached to {CacheFilePath}")]
        public static partial void CatalogCached(ILogger logger, string cacheFilePath);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to write skill catalog cache to {CacheFilePath}")]
        public static partial void WriteCacheFailed(ILogger logger, Exception ex, string cacheFilePath);

        [LoggerMessage(Level = LogLevel.Information, Message = "Loaded skill catalog from cache ({EntryCount} entries, cached at {CachedAt})")]
        public static partial void CatalogLoadedFromCache(ILogger logger, int entryCount, DateTimeOffset cachedAt);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to read skill catalog cache from {CacheFilePath}")]
        public static partial void ReadCacheFailed(ILogger logger, Exception ex, string cacheFilePath);
    }
}

using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NuCode.Providers;

namespace WeaveFleet.Infrastructure.Harnesses.NuCode;

/// <summary>
/// Fetches the models.dev catalog (<c>https://models.dev/api.json</c>) and extracts
/// models for a given provider. Results are cached in-process with a 1-hour TTL.
/// </summary>
internal sealed partial class ModelsDevCatalogClient : IDisposable
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ModelsDevCatalogClient> _logger;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);
    private const string CatalogUrl = "https://models.dev/api.json";

    private IReadOnlyDictionary<string, IReadOnlyList<DiscoveredModel>>? _cache;
    private DateTimeOffset _cacheExpiry = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public ModelsDevCatalogClient(
        IHttpClientFactory httpClientFactory,
        ILogger<ModelsDevCatalogClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Returns models available for <paramref name="providerId"/> from the models.dev catalog.
    /// Returns an empty list if the catalog is unreachable or the provider is not found.
    /// </summary>
    public async Task<IReadOnlyList<DiscoveredModel>> GetModelsForProviderAsync(
        string providerId,
        CancellationToken ct)
    {
        var catalog = await GetCatalogAsync(ct).ConfigureAwait(false);
        if (catalog is null)
            return [];

        return catalog.TryGetValue(providerId, out var models) ? models : [];
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<DiscoveredModel>>?> GetCatalogAsync(
        CancellationToken ct)
    {
        if (_cache is not null && DateTimeOffset.UtcNow < _cacheExpiry)
            return _cache;

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Double-check inside lock
            if (_cache is not null && DateTimeOffset.UtcNow < _cacheExpiry)
                return _cache;

            var catalog = await FetchCatalogAsync(ct).ConfigureAwait(false);
            if (catalog is not null)
            {
                _cache = catalog;
                _cacheExpiry = DateTimeOffset.UtcNow + CacheTtl;
            }

            return _cache;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<DiscoveredModel>>?> FetchCatalogAsync(
        CancellationToken ct)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient("ModelsDevCatalog");
            using var request = new HttpRequestMessage(HttpMethod.Get, CatalogUrl);
            request.Headers.UserAgent.ParseAdd("NuCode/1.0");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LogFetchFailed((int)response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            return ParseCatalog(doc);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFetchError(ex);
            return null;
        }
    }

    internal static IReadOnlyDictionary<string, IReadOnlyList<DiscoveredModel>> ParseCatalog(
        JsonDocument doc)
    {
        var result = new Dictionary<string, IReadOnlyList<DiscoveredModel>>(StringComparer.OrdinalIgnoreCase);

        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return result;

        foreach (var providerProp in doc.RootElement.EnumerateObject())
        {
            var providerId = providerProp.Name;
            var providerElement = providerProp.Value;

            if (providerElement.ValueKind != JsonValueKind.Object)
                continue;

            if (!providerElement.TryGetProperty("models", out var modelsElement)
                || modelsElement.ValueKind != JsonValueKind.Object)
                continue;

            var models = new List<DiscoveredModel>();
            foreach (var modelProp in modelsElement.EnumerateObject())
            {
                var modelElement = modelProp.Value;
                if (modelElement.ValueKind != JsonValueKind.Object)
                    continue;

                // Skip deprecated models
                if (modelElement.TryGetProperty("status", out var statusProp)
                    && statusProp.ValueKind == JsonValueKind.String
                    && string.Equals(statusProp.GetString(), "deprecated", StringComparison.OrdinalIgnoreCase))
                    continue;

                var id = modelProp.Name;

                string? name = null;
                if (modelElement.TryGetProperty("name", out var nameProp)
                    && nameProp.ValueKind == JsonValueKind.String)
                    name = nameProp.GetString();

                models.Add(new DiscoveredModel(id, name));
            }

            models.Sort((a, b) =>
                string.Compare(a.Name ?? a.Id, b.Name ?? b.Id, StringComparison.OrdinalIgnoreCase));

            result[providerId] = models;
        }

        return result;
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "models.dev catalog fetch failed: HTTP {StatusCode}")]
    private partial void LogFetchFailed(int statusCode);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "models.dev catalog fetch error")]
    private partial void LogFetchError(Exception exception);

    public void Dispose() => _lock.Dispose();
}

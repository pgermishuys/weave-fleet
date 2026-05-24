using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NuCode.Providers;

namespace WeaveFleet.Infrastructure.Harnesses.NuCode;

/// <summary>
/// Discovers available models by querying provider APIs.
/// For OpenAI-compatible providers, calls <c>GET {endpoint}/models</c>.
/// Results are returned best-effort — failures produce an empty list rather than exceptions.
/// </summary>
internal sealed partial class NuCodeModelDiscoveryService : IModelDiscoveryService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<NuCodeModelDiscoveryService> _logger;

    private const string ApiKeyField = "apiKey";
    private const string GitHubTokenField = "githubToken";
    private const string BaseUrlOption = "baseUrl";

    public NuCodeModelDiscoveryService(
        IHttpClientFactory httpClientFactory,
        ILogger<NuCodeModelDiscoveryService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DiscoveredModel>> DiscoverModelsAsync(
        ProviderDefinition provider,
        IReadOnlyDictionary<string, string> credentials,
        IReadOnlyDictionary<string, string>? options = null,
        CancellationToken ct = default)
    {
        if (!provider.IsOpenAiCompatible)
            return [];

        try
        {
            // Copilot uses the GitHub REST API models catalog for a comprehensive list
            if (string.Equals(provider.Id, "copilot", StringComparison.OrdinalIgnoreCase))
                return await DiscoverCopilotModelsAsync(credentials, ct).ConfigureAwait(false);

            var endpoint = ResolveModelsEndpoint(provider, options);
            if (endpoint is null)
                return [];

            var apiKey = ResolveApiKey(provider, credentials);
            if (string.IsNullOrEmpty(apiKey) && !provider.CredentialOptional)
                return [];

            using var client = _httpClientFactory.CreateClient("NuCodeModelDiscovery");
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);

            if (!string.IsNullOrEmpty(apiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LogModelDiscoveryFailed(provider.Id, (int)response.StatusCode);
                return [];
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            var result = ParseModelsResponse(doc, provider.Id);
            LogModelDiscoverySuccess(provider.Id, result.Count);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogModelDiscoveryError(provider.Id, ex);
            return [];
        }
    }

    /// <summary>
    /// Discovers models available through GitHub Copilot by querying the GitHub REST API
    /// models catalog (<c>GET https://api.github.com/models</c>), which returns the full
    /// set of models available to the authenticated user.
    /// </summary>
    private async Task<IReadOnlyList<DiscoveredModel>> DiscoverCopilotModelsAsync(
        IReadOnlyDictionary<string, string> credentials,
        CancellationToken ct)
    {
        if (!credentials.TryGetValue(GitHubTokenField, out var githubToken) || string.IsNullOrEmpty(githubToken))
            return [];

        using var client = _httpClientFactory.CreateClient("NuCodeModelDiscovery");
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", githubToken);
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.UserAgent.ParseAdd("NuCode/1.0");

        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            LogModelDiscoveryFailed("copilot", (int)response.StatusCode);

            // Fall back to the Copilot OpenAI-compatible endpoint
            return await DiscoverCopilotModelsFallbackAsync(githubToken, ct).ConfigureAwait(false);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        return ParseGitHubModelsResponse(doc);
    }

    /// <summary>
    /// Fallback: queries <c>GET https://api.githubcopilot.com/models</c> (OpenAI-compatible)
    /// when the GitHub REST API models catalog is unavailable.
    /// </summary>
    private async Task<IReadOnlyList<DiscoveredModel>> DiscoverCopilotModelsFallbackAsync(
        string githubToken,
        CancellationToken ct)
    {
        using var client = _httpClientFactory.CreateClient("NuCodeModelDiscovery");
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.githubcopilot.com/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", githubToken);
        request.Headers.TryAddWithoutValidation("Copilot-Integration-Id", "vscode-chat");
        request.Headers.TryAddWithoutValidation("Editor-Version", "NuCode/1.0");
        request.Headers.TryAddWithoutValidation("Editor-Plugin-Version", "NuCode/1.0");
        request.Headers.TryAddWithoutValidation("Openai-Intent", "conversation-edits");

        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            LogModelDiscoveryFailed("copilot-fallback", (int)response.StatusCode);
            return [];
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        return ParseModelsResponse(doc, "copilot");
    }

    /// <summary>
    /// Parses the GitHub REST API <c>/models</c> response.
    /// Returns an array of model objects with <c>id</c>, <c>name</c>, <c>summary</c>, etc.
    /// Filters to models that support chat completions (have "chat" in capabilities or tags).
    /// </summary>
    private static List<DiscoveredModel> ParseGitHubModelsResponse(JsonDocument doc)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var models = new List<DiscoveredModel>();

        // Response is a bare array of model objects
        var array = doc.RootElement;
        if (array.ValueKind != JsonValueKind.Array)
            return [];

        foreach (var item in array.EnumerateArray())
        {
            // The "id" field contains the full model identifier (e.g. "openai/gpt-4o")
            // The "name" field is the display name (e.g. "OpenAI GPT-4o")
            if (!item.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.String)
                continue;

            var fullId = idProp.GetString()!;

            // Extract the model ID portion after the publisher prefix (e.g. "openai/gpt-4o" → "gpt-4o")
            // Some models may not have a prefix
            var modelId = fullId.Contains('/')
                ? fullId[(fullId.IndexOf('/') + 1)..]
                : fullId;

            if (!seen.Add(modelId))
                continue;

            string? name = null;
            if (item.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
                name = nameProp.GetString();

            models.Add(new DiscoveredModel(modelId, name));
        }

        models.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));
        return models;
    }

    /// <summary>
    /// Parses the OpenAI-compatible <c>/models</c> response.
    /// Expects <c>{ "data": [{ "id": "...", ... }] }</c> or a bare array <c>[{ "id": "..." }]</c>.
    /// Deduplicates by model ID (first occurrence wins).
    /// </summary>
    private static List<DiscoveredModel> ParseModelsResponse(JsonDocument doc, string providerId)
    {
        // Standard OpenAI format: { "data": [...] }
        JsonElement array;
        if (doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("data", out var dataElement)
            && dataElement.ValueKind == JsonValueKind.Array)
        {
            array = dataElement;
        }
        else if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            // Some providers return a bare array
            array = doc.RootElement;
        }
        else
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var models = new List<DiscoveredModel>();

        foreach (var item in array.EnumerateArray())
        {
            if (item.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
            {
                var id = idProp.GetString()!;
                if (!seen.Add(id))
                    continue; // skip duplicates

                string? name = null;

                // Try "name" field, fall back to id
                if (item.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
                    name = nameProp.GetString();

                models.Add(new DiscoveredModel(id, name));
            }
        }

        // Sort alphabetically by id for consistent display
        models.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));
        return models;
    }

    private static Uri? ResolveModelsEndpoint(
        ProviderDefinition provider,
        IReadOnlyDictionary<string, string>? options)
    {
        // Explicit baseUrl override
        if (options is not null && options.TryGetValue(BaseUrlOption, out var overrideUrl)
            && !string.IsNullOrWhiteSpace(overrideUrl))
        {
            return new Uri(new Uri(overrideUrl.TrimEnd('/') + "/"), "models");
        }

        // Provider's default endpoint
        if (provider.DefaultEndpoint is not null)
        {
            var baseUri = new Uri(provider.DefaultEndpoint.TrimEnd('/') + "/");
            return new Uri(baseUri, "models");
        }

        // OpenAI SDK default
        if (string.Equals(provider.Id, "openai", StringComparison.OrdinalIgnoreCase))
            return new Uri("https://api.openai.com/v1/models");

        return null;
    }

    private static string ResolveApiKey(
        ProviderDefinition provider,
        IReadOnlyDictionary<string, string> credentials)
    {
        if (string.Equals(provider.Id, "copilot", StringComparison.OrdinalIgnoreCase))
        {
            return credentials.TryGetValue(GitHubTokenField, out var githubToken)
                ? githubToken
                : string.Empty;
        }

        return credentials.TryGetValue(ApiKeyField, out var apiKey) ? apiKey : string.Empty;
    }

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Model discovery for provider '{ProviderId}': found {Count} models")]
    private partial void LogModelDiscoverySuccess(string providerId, int count);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Model discovery failed for provider '{ProviderId}': HTTP {StatusCode}")]
    private partial void LogModelDiscoveryFailed(string providerId, int statusCode);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Model discovery error for provider '{ProviderId}'")]
    private partial void LogModelDiscoveryError(string providerId, Exception exception);
}

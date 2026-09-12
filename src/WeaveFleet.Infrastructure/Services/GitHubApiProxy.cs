using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace WeaveFleet.Infrastructure.Services;

/// <summary>Result of a conditional GitHub API request.</summary>
/// <param name="StatusCode">The HTTP status, reported as 200 when the cached body was reused.</param>
/// <param name="Body">The parsed body, or <c>null</c> on failure.</param>
/// <param name="NotModified">True when GitHub answered 304 and the cached body was returned.</param>
/// <param name="RateLimitResetAt">When the rate limit resets, if the request was refused for exceeding it.</param>
public sealed record GitHubApiResponse(
    HttpStatusCode StatusCode,
    JsonNode? Body,
    bool NotModified,
    DateTimeOffset? RateLimitResetAt = null)
{
    public bool IsSuccess => Body is not null;
}

/// <summary>
/// Proxies authenticated requests to the GitHub REST API.
/// </summary>
public sealed class GitHubApiProxy(IHttpClientFactory httpClientFactory)
{
    private const string BaseUrl = "https://api.github.com";
    private const int MaxLogBytes = 5 * 1024 * 1024; // 5 MB cap for log responses
    private const int MaxCachedResponses = 2000;

    // ETag and body per (token, path). GitHub doesn't count 304 responses against the rate limit.
    private readonly ConcurrentDictionary<string, (string ETag, string Body)> _conditionalCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Sends a GET with <c>If-None-Match</c> when a previous response for the same token and path was cached,
    /// and reuses the cached body on 304.
    /// </summary>
    public async Task<GitHubApiResponse> GetConditionalAsync(string token, string path, CancellationToken ct = default)
    {
        var key = ConditionalCacheKey(token, path);
        using var client = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/{path.TrimStart('/')}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/vnd.github.v3+json");
        request.Headers.UserAgent.ParseAdd("fleet/1.0");

        var hasCached = _conditionalCache.TryGetValue(key, out var cached);
        if (hasCached && EntityTagHeaderValue.TryParse(cached.ETag, out var etag))
            request.Headers.IfNoneMatch.Add(etag);

        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotModified && hasCached)
            return new GitHubApiResponse(HttpStatusCode.OK, JsonNode.Parse(cached.Body), NotModified: true);

        if (!response.IsSuccessStatusCode)
            return new GitHubApiResponse(response.StatusCode, null, NotModified: false, ReadRateLimitReset(response));

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var responseETag = response.Headers.ETag?.ToString();
        if (responseETag is not null)
        {
            if (_conditionalCache.Count >= MaxCachedResponses)
                _conditionalCache.Clear();
            _conditionalCache[key] = (responseETag, json);
        }

        return new GitHubApiResponse(response.StatusCode, JsonNode.Parse(json), NotModified: false);
    }

    private static string ConditionalCacheKey(string token, string path)
        => $"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)))[..16]}:{path}";

    private static DateTimeOffset? ReadRateLimitReset(HttpResponseMessage response)
    {
        if (response.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests))
            return null;

        if (response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining)
            && remaining.FirstOrDefault() == "0"
            && response.Headers.TryGetValues("x-ratelimit-reset", out var reset)
            && long.TryParse(reset.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var epochSeconds))
        {
            return DateTimeOffset.FromUnixTimeSeconds(epochSeconds);
        }

        // Secondary rate limits use Retry-After instead.
        if (response.Headers.RetryAfter?.Delta is { } delta)
            return DateTimeOffset.UtcNow.Add(delta);

        return response.StatusCode == HttpStatusCode.TooManyRequests ? DateTimeOffset.UtcNow.AddMinutes(1) : null;
    }

    /// <summary>Sends an authenticated request to the GitHub API and returns the JSON response.</summary>
    public async Task<JsonNode?> FetchAsync(
        string token,
        string path,
        string method = "GET",
        JsonNode? body = null,
        CancellationToken ct = default)
    {
        using var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github.v3+json");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("fleet/1.0");

        var url = $"{BaseUrl}/{path.TrimStart('/')}";

        HttpResponseMessage response;
        if (method.Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            response = await client.GetAsync(url, ct).ConfigureAwait(false);
        }
        else
        {
            var content = body is null
                ? new StringContent("{}", Encoding.UTF8, "application/json")
                : new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            response = await client.SendAsync(
                new HttpRequestMessage(new HttpMethod(method), url) { Content = content },
                ct).ConfigureAwait(false);
        }

        if (!response.IsSuccessStatusCode)
            return null;

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonNode.Parse(json);
    }

    /// <summary>
    /// Fetches plain-text content from the GitHub API, manually following redirects without
    /// forwarding the Authorization header to the redirect target (e.g. Azure Blob Storage).
    /// Caps the response at 5 MB to prevent OOM on large log files.
    /// </summary>
    public async Task<string?> FetchTextAsync(
        string token,
        string path,
        CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/{path.TrimStart('/')}";

        // Step 1: request to GitHub with auth, but do NOT auto-follow redirects so the
        // Bearer token is never forwarded to the external redirect target.
        using var noRedirectHandler = new HttpClientHandler { AllowAutoRedirect = false };
        using var githubClient = new HttpClient(noRedirectHandler);
        githubClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        githubClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github.v3+json");
        githubClient.DefaultRequestHeaders.UserAgent.ParseAdd("fleet/1.0");

        using var firstResponse = await githubClient
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        Uri? downloadUri;
        if (firstResponse.StatusCode is HttpStatusCode.Found or HttpStatusCode.MovedPermanently
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
        {
            downloadUri = firstResponse.Headers.Location;
            if (downloadUri is null)
                return null;
        }
        else if (firstResponse.IsSuccessStatusCode)
        {
            // No redirect — read directly (no auth header to leak)
            downloadUri = null;
        }
        else
        {
            return null;
        }

        // Step 2: fetch the actual content from the (possibly external) download URL,
        // deliberately without the GitHub Authorization header.
        using var downloadClient = httpClientFactory.CreateClient();
        downloadClient.DefaultRequestHeaders.UserAgent.ParseAdd("fleet/1.0");

        var targetUri = downloadUri ?? new Uri(url);
        using var downloadResponse = await downloadClient
            .GetAsync(targetUri, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (!downloadResponse.IsSuccessStatusCode)
            return null;

        // Step 3: read with a 5 MB cap to prevent OOM on large log files.
        await using var stream = await downloadResponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[MaxLogBytes];
        var totalRead = 0;
        int bytesRead;
        while (totalRead < MaxLogBytes
               && (bytesRead = await stream.ReadAsync(buffer.AsMemory(totalRead, MaxLogBytes - totalRead), ct).ConfigureAwait(false)) > 0)
        {
            totalRead += bytesRead;
        }

        return Encoding.UTF8.GetString(buffer, 0, totalRead);
    }

    /// <summary>
    /// Sends an authenticated GraphQL request to the GitHub GraphQL API and returns the parsed JSON response.
    /// Returns <c>null</c> on failure.
    /// </summary>
    public async Task<JsonNode?> PostGraphQLAsync(
        string token,
        string query,
        JsonObject variables,
        CancellationToken ct)
    {
        using var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("fleet/1.0");

        var payload = new JsonObject
        {
            ["query"] = JsonValue.Create(query),
            ["variables"] = variables,
        };

        var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync($"{BaseUrl}/graphql", content, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            return null;

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonNode.Parse(json);
    }
}

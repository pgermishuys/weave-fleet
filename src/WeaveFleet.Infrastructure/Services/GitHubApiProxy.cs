using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace WeaveFleet.Infrastructure.Services;

/// <summary>
/// Proxies authenticated requests to the GitHub REST API.
/// </summary>
public sealed class GitHubApiProxy(IHttpClientFactory httpClientFactory)
{
    private const string BaseUrl = "https://api.github.com";
    private const int MaxLogBytes = 5 * 1024 * 1024; // 5 MB cap for log responses

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

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace WeaveFleet.Infrastructure.Services;

/// <summary>
/// Proxies authenticated requests to the GitHub REST API.
/// </summary>
public sealed class GitHubApiProxy(IHttpClientFactory httpClientFactory)
{
    private const string BaseUrl = "https://api.github.com";

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

        return await response.Content.ReadFromJsonAsync<JsonNode>(ct).ConfigureAwait(false);
    }
}

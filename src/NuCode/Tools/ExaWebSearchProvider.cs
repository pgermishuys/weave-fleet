using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace NuCode.Tools;

/// <summary>
/// Web search provider using Exa AI REST API (https://api.exa.ai/search).
/// Requires an API key via ExaWebSearchOptions.
/// </summary>
internal sealed class ExaWebSearchProvider(HttpClient httpClient, string apiKey) : IWebSearchProvider
{
    private const string BaseUrl = "https://api.exa.ai/search";

    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(
        string query,
        int count = 10,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl);
        request.Headers.Add("x-api-key", apiKey);
        request.Content = JsonContent.Create(new ExaSearchRequest
        {
            Query = query,
            NumResults = count,
            Contents = new ExaContents { Text = new ExaTextOptions { MaxCharacters = 300 } },
        });

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<ExaSearchResponse>(cancellationToken);
        if (result?.Results is null)
        {
            return [];
        }

        return result.Results.Select(r => new WebSearchResult
        {
            Title = r.Title ?? "",
            Url = r.Url ?? "",
            Snippet = r.Text ?? "",
        }).ToList();
    }

    private sealed class ExaSearchRequest
    {
        [JsonPropertyName("query")]
        public string Query { get; set; } = "";

        [JsonPropertyName("numResults")]
        public int NumResults { get; set; }

        [JsonPropertyName("contents")]
        public ExaContents? Contents { get; set; }
    }

    private sealed class ExaContents
    {
        [JsonPropertyName("text")]
        public ExaTextOptions? Text { get; set; }
    }

    private sealed class ExaTextOptions
    {
        [JsonPropertyName("maxCharacters")]
        public int MaxCharacters { get; set; }
    }

    private sealed class ExaSearchResponse
    {
        [JsonPropertyName("results")]
        public List<ExaResult>? Results { get; set; }
    }

    private sealed class ExaResult
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }
}

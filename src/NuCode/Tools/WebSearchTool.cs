using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.AI;

namespace NuCode.Tools;

/// <summary>
/// Searches the web for information using the configured IWebSearchProvider.
/// </summary>
internal sealed class WebSearchTool(IWebSearchProvider searchProvider) : INuCodeTool
{
    public string Name => "websearch";
    public string Description => "Search the web for information. Returns titles, URLs, and snippets from search results.";

    public AIFunction ToAIFunction() =>
        AIFunctionFactory.Create(ExecuteAsync, new AIFunctionFactoryOptions
        {
            Name = Name,
            Description = Description,
        });

    [Description("Search the web for information. Returns titles, URLs, and snippets from search results.")]
    internal async Task<string> ExecuteAsync(
        [Description("The search query")] string query,
        [Description("Maximum number of results to return (default 10, max 25)")] int? count = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return "Error: query is required.";
        }

        var resultCount = Math.Clamp(count ?? 10, 1, 25);

        IReadOnlyList<WebSearchResult> results;
        try
        {
            results = await searchProvider.SearchAsync(query, resultCount, cancellationToken);
        }
        catch (Exception ex)
        {
            return $"Error performing web search: {ex.Message}";
        }

        if (results.Count == 0)
        {
            return "No results found.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Found {results.Count} result(s) for: {query}");
        sb.AppendLine();

        foreach (var result in results)
        {
            sb.AppendLine($"### {result.Title}");
            sb.AppendLine($"URL: {result.Url}");
            if (!string.IsNullOrWhiteSpace(result.Snippet))
            {
                sb.AppendLine(result.Snippet);
            }
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }
}

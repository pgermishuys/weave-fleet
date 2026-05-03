namespace NuCode.Tools;

/// <summary>
/// Abstraction for web search providers. Consumers implement this to provide
/// search capabilities (Exa, Bing, Google, etc.).
/// </summary>
public interface IWebSearchProvider
{
    /// <summary>
    /// Performs a web search.
    /// </summary>
    /// <param name="query">The search query.</param>
    /// <param name="count">Maximum number of results to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Search results.</returns>
    Task<IReadOnlyList<WebSearchResult>> SearchAsync(
        string query,
        int count = 10,
        CancellationToken cancellationToken = default);
}

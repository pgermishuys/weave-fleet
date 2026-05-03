namespace NuCode.Tools;

/// <summary>
/// Represents a single web search result.
/// </summary>
public sealed record WebSearchResult
{
    /// <summary>Title of the search result.</summary>
    public required string Title { get; init; }

    /// <summary>URL of the search result.</summary>
    public required string Url { get; init; }

    /// <summary>A snippet/summary of the result content.</summary>
    public required string Snippet { get; init; }
}

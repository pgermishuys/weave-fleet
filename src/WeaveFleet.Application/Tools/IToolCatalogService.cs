using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Tools;

namespace WeaveFleet.Application.Tools;

/// <summary>
/// Service for fetching and caching the public tool catalog.
/// </summary>
public interface IToolCatalogService
{
    /// <summary>
    /// Fetches the tool catalog from the remote source.
    /// Returns cached data if the remote fetch fails and a cache exists.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing the catalog entries and a staleness indicator.</returns>
    Task<Result<ToolCatalogResponse>> FetchCatalogAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Response from the catalog service containing entries and cache metadata.
/// </summary>
public sealed record ToolCatalogResponse(
    IReadOnlyList<ToolCatalogEntry> Entries,
    bool IsStale,
    DateTimeOffset? CachedAt);

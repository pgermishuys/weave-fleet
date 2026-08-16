using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Skills;

namespace WeaveFleet.Application.Skills;

/// <summary>
/// Service for fetching and caching the public skill catalog.
/// </summary>
public interface ISkillCatalogService
{
    /// <summary>
    /// Fetches the skill catalog from the remote source.
    /// Returns cached data if the remote fetch fails and a cache exists.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing the catalog entries and a staleness indicator.</returns>
    Task<Result<CatalogResponse>> FetchCatalogAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Response from the catalog service containing entries and cache metadata.
/// </summary>
public sealed record CatalogResponse(
    IReadOnlyList<CatalogEntry> Entries,
    bool IsStale,
    DateTimeOffset? CachedAt);

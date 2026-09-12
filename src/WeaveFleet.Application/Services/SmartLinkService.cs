using WeaveFleet.Application.DTOs;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Services;

/// <summary>
/// Wakes the background service that finds and refreshes smart links.
/// </summary>
public interface ISmartLinkWatcher
{
    /// <summary>Asks the watcher to run a cycle now instead of waiting for its next tick.</summary>
    void Wake();
}

public sealed class SmartLinkService(
    ISmartLinkRepository smartLinkRepository,
    ISessionRepository sessionRepository,
    IUserContext userContext,
    ISmartLinkWatcher watcher)
{
    public async Task<IReadOnlyList<SmartLinkDto>> ListBySessionIdAsync(string sessionId)
    {
        if (!await OwnsSessionAsync(sessionId))
            return Array.Empty<SmartLinkDto>();

        var links = await smartLinkRepository.ListActiveBySessionIdAsync(sessionId);
        return links.Select(ToDto).ToList();
    }

    public async Task<IReadOnlyList<SmartLinkDto>> ListAllBySessionIdAsync(string sessionId)
    {
        if (!await OwnsSessionAsync(sessionId))
            return Array.Empty<SmartLinkDto>();

        // The watcher only backfills origins for running sessions; opening any other session fills in its own.
        if (await smartLinkRepository.InsertMissingSourceLinksAsync(sessionId, CancellationToken.None) > 0)
            watcher.Wake();

        var links = await smartLinkRepository.ListBySessionIdAsync(sessionId);
        return links.Select(ToDto).ToList();
    }

    /// <summary>
    /// Attaches a GitHub pull request or issue to a session as a pinned link, showing it again if it
    /// was dismissed.
    /// </summary>
    public async Task<AddSmartLinkResult> AddAsync(string sessionId, string url, CancellationToken ct = default)
    {
        if (!GitHubLinkParser.TryParseUrl(url.Trim(), out var reference))
            return AddSmartLinkResult.InvalidUrl;

        if (!await OwnsSessionAsync(sessionId))
            return AddSmartLinkResult.NotFound;

        var link = new SmartLink
        {
            Id = Guid.NewGuid().ToString(),
            SessionId = sessionId,
            Url = reference.Url,
            ProviderId = "github",
            ResourceType = reference.ResourceType,
            ResourceId = reference.ResourceId,
            Title = reference.ResourceId,
            UserId = userContext.UserId,
            Relationship = SmartLinkRelationships.Pinned,
        };

        await smartLinkRepository.InsertDetectedAsync(link, restoreDismissed: true, ct);
        watcher.Wake();

        var stored = (await smartLinkRepository.ListBySessionIdAsync(sessionId))
            .FirstOrDefault(l => string.Equals(l.ResourceId, reference.ResourceId, StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(l.Url, reference.Url, StringComparison.Ordinal));
        return stored is null ? AddSmartLinkResult.NotFound : AddSmartLinkResult.Added(ToDto(stored));
    }

    /// <summary>Pins a mentioned link, or returns a pinned link to mentioned.</summary>
    public async Task<bool> SetPinnedAsync(string sessionId, string linkId, bool pinned)
    {
        if (!await OwnsSessionAsync(sessionId))
            return false;

        var link = (await smartLinkRepository.ListBySessionIdAsync(sessionId)).FirstOrDefault(l => l.Id == linkId);
        if (link is null)
            return false;

        // Origin and own links already show in the header; pinning only moves links between these two.
        if (link.Relationship is not (SmartLinkRelationships.Mentioned or SmartLinkRelationships.Pinned))
            return true;

        return await smartLinkRepository.SetRelationshipAsync(
            linkId,
            pinned ? SmartLinkRelationships.Pinned : SmartLinkRelationships.Mentioned);
    }

    /// <summary>Asks the watcher to re-check every link on the session now.</summary>
    public async Task<bool> RefreshAsync(string sessionId)
    {
        if (!await OwnsSessionAsync(sessionId))
            return false;

        await smartLinkRepository.MarkSessionDueAsync(sessionId);
        watcher.Wake();
        return true;
    }

    public async Task<bool> DismissAsync(string sessionId, string linkId)
    {
        if (!await OwnsSessionAsync(sessionId))
            return false;

        await smartLinkRepository.DismissAsync(linkId);
        return true;
    }

    private async Task<bool> OwnsSessionAsync(string sessionId)
    {
        var session = await sessionRepository.GetByIdAsync(sessionId);
        return session is not null && string.Equals(session.UserId, userContext.UserId, StringComparison.Ordinal);
    }

    public static SmartLinkDto ToDto(SmartLink link) => new(
        link.Id,
        link.SessionId,
        link.Url,
        link.ProviderId,
        link.ResourceType,
        link.ResourceId,
        link.Title,
        link.Status,
        link.StatusLabel,
        link.MetadataJson,
        link.IsDismissed,
        link.IsTerminal,
        link.CreatedAt,
        link.UpdatedAt,
        link.Relationship,
        link.EnrichmentStatus,
        link.LastCheckedAt);
}

/// <summary>Outcome of attaching a link to a session.</summary>
public sealed record AddSmartLinkResult(SmartLinkDto? Link, bool IsInvalidUrl)
{
    public static readonly AddSmartLinkResult InvalidUrl = new(null, true);
    public static readonly AddSmartLinkResult NotFound = new(null, false);
    public static AddSmartLinkResult Added(SmartLinkDto link) => new(link, false);
}

using WeaveFleet.Application.DTOs;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Services;

public sealed class SmartLinkService(
    ISmartLinkRepository smartLinkRepository,
    ISessionRepository sessionRepository,
    IUserContext userContext)
{
    public async Task<IReadOnlyList<SmartLinkDto>> ListBySessionIdAsync(string sessionId)
    {
        var session = await sessionRepository.GetByIdAsync(sessionId);
        if (session is null || !string.Equals(session.UserId, userContext.UserId, StringComparison.Ordinal))
            return Array.Empty<SmartLinkDto>();

        var links = await smartLinkRepository.ListActiveBySessionIdAsync(sessionId);
        return links.Select(ToDto).ToList();
    }

    public async Task<IReadOnlyList<SmartLinkDto>> ListAllBySessionIdAsync(string sessionId)
    {
        var session = await sessionRepository.GetByIdAsync(sessionId);
        if (session is null || !string.Equals(session.UserId, userContext.UserId, StringComparison.Ordinal))
            return Array.Empty<SmartLinkDto>();

        var links = await smartLinkRepository.ListBySessionIdAsync(sessionId);
        return links.Select(ToDto).ToList();
    }

    public async Task<SmartLinkDto?> UpsertAsync(string sessionId, UpsertSmartLinkRequest request)
    {
        var session = await sessionRepository.GetByIdAsync(sessionId);
        if (session is null || !string.Equals(session.UserId, userContext.UserId, StringComparison.Ordinal))
            return null;

        var now = DateTime.UtcNow.ToString("O");

        var existing = await smartLinkRepository.GetBySessionIdAndUrlAsync(sessionId, request.Url);

        var smartLink = existing ?? new SmartLink
        {
            Id = Guid.NewGuid().ToString(),
            SessionId = sessionId,
            Url = request.Url,
            CreatedAt = now,
            UserId = userContext.UserId
        };

        smartLink.ProviderId = request.ProviderId;
        smartLink.ResourceType = request.ResourceType;
        smartLink.ResourceId = request.ResourceId;
        smartLink.Title = request.Title;
        smartLink.Status = request.Status;
        smartLink.StatusLabel = request.StatusLabel;
        smartLink.MetadataJson = request.MetadataJson;
        smartLink.IsTerminal = request.IsTerminal;
        smartLink.UpdatedAt = now;

        await smartLinkRepository.UpsertAsync(smartLink);
        return ToDto(smartLink);
    }

    public async Task<bool> BulkUpsertAsync(string sessionId, IReadOnlyList<UpsertSmartLinkRequest> requests)
    {
        var session = await sessionRepository.GetByIdAsync(sessionId);
        if (session is null || !string.Equals(session.UserId, userContext.UserId, StringComparison.Ordinal))
            return false;

        foreach (var request in requests)
            await UpsertAsync(sessionId, request);

        return true;
    }

    public async Task<bool> DismissAsync(string sessionId, string linkId)
    {
        var session = await sessionRepository.GetByIdAsync(sessionId);
        if (session is null || !string.Equals(session.UserId, userContext.UserId, StringComparison.Ordinal))
            return false;

        await smartLinkRepository.DismissAsync(linkId);
        return true;
    }

    private static SmartLinkDto ToDto(SmartLink link) => new(
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
        link.UpdatedAt);
}

namespace WeaveFleet.Application.DTOs;

public sealed record SmartLinkDto(
    string Id,
    string SessionId,
    string Url,
    string ProviderId,
    string ResourceType,
    string ResourceId,
    string Title,
    string Status,
    string StatusLabel,
    string? MetadataJson,
    bool IsDismissed,
    bool IsTerminal,
    string CreatedAt,
    string UpdatedAt,
    string Relationship,
    string EnrichmentStatus,
    string? LastCheckedAt);

/// <summary>Attaches a GitHub pull request or issue to a session.</summary>
public sealed record AddSmartLinkRequest(string Url);

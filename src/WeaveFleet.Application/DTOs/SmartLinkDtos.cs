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
    string UpdatedAt);

public sealed record UpsertSmartLinkRequest(
    string Url,
    string ProviderId,
    string ResourceType,
    string ResourceId,
    string Title,
    string Status,
    string StatusLabel,
    string? MetadataJson,
    bool IsTerminal);

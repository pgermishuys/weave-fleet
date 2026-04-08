namespace WeaveFleet.Application.DTOs;

public sealed record DelegationDto(
    string DelegationId,
    string? ParentToolCallId,
    string? ChildSessionId,
    string Title,
    string Status);

public sealed record DelegationEventDto(
    string DelegationId,
    string ParentSessionId,
    string? ParentToolCallId,
    string? ChildSessionId,
    string Title,
    string Status);

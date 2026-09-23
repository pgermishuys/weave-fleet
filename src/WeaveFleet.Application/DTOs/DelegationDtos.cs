namespace WeaveFleet.Application.DTOs;

public sealed record DelegationDto(
    string DelegationId,
    string? ParentToolCallId,
    string? ChildSessionId,
    string Title,
    string Status,
    string CreatedAt);

/// <param name="Background">
/// The call that started the delegation returned while its child carries on working: the parent is free, and the
/// child's work doesn't count as the parent's.
/// </param>
public sealed record DelegationEventDto(
    string DelegationId,
    string ParentSessionId,
    string? ParentToolCallId,
    string? ChildSessionId,
    string Title,
    string Status,
    string CreatedAt,
    bool? Background = null);

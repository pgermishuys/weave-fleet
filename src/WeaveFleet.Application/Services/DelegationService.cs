using WeaveFleet.Application.DTOs;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Services;

public sealed class DelegationService(
    IDelegationRepository delegationRepository,
    IEventBroadcaster eventBroadcaster)
{
    private static readonly HashSet<string> TerminalStatuses = new(StringComparer.Ordinal)
    {
        "completed",
        "error",
        "cancelled"
    };

    public async Task<DelegationDto> HandleDelegationDetectedAsync(
        string parentSessionId,
        string parentToolCallId,
        string title)
    {
        ValidateRequired(parentSessionId, nameof(parentSessionId));
        ValidateRequired(parentToolCallId, nameof(parentToolCallId));
        ValidateRequired(title, nameof(title));

        var existing = await delegationRepository.GetByParentToolCallIdAsync(parentSessionId, parentToolCallId);
        if (existing is not null)
            return ToDto(existing);

        var now = DateTime.UtcNow.ToString("O");
        var delegation = new Delegation
        {
            Id = Guid.NewGuid().ToString(),
            ParentSessionId = parentSessionId,
            ParentToolCallId = parentToolCallId,
            Title = title,
            Status = "pending",
            CreatedAt = now,
            UpdatedAt = now
        };

        await delegationRepository.InsertAsync(delegation);
        await BroadcastAsync(parentSessionId, "delegation.created", delegation);
        return ToDto(delegation);
    }

    public async Task<DelegationDto?> HandleChildLinkedAsync(
        string parentSessionId,
        string parentToolCallId,
        string childSessionId)
    {
        ValidateRequired(parentSessionId, nameof(parentSessionId));
        ValidateRequired(parentToolCallId, nameof(parentToolCallId));
        ValidateRequired(childSessionId, nameof(childSessionId));

        var delegation = await delegationRepository.GetByParentToolCallIdAsync(parentSessionId, parentToolCallId);
        if (delegation is null)
            return null;

        if (delegation.Status != "pending" && delegation.Status != "running")
            throw new InvalidOperationException($"Cannot link child session when delegation is '{delegation.Status}'.");

        var shouldUpdateChild = !string.Equals(delegation.ChildSessionId, childSessionId, StringComparison.Ordinal);
        var shouldUpdateStatus = delegation.Status != "running";

        if (!shouldUpdateChild && !shouldUpdateStatus)
            return ToDto(delegation);

        var now = DateTime.UtcNow.ToString("O");
        if (shouldUpdateChild)
            await delegationRepository.UpdateChildSessionIdAsync(delegation.Id, childSessionId, now);

        if (shouldUpdateStatus)
            await delegationRepository.UpdateStatusAsync(delegation.Id, "running", now, null);

        delegation.ChildSessionId = childSessionId;
        delegation.Status = "running";
        delegation.UpdatedAt = now;
        delegation.CompletedAt = null;

        await BroadcastAsync(parentSessionId, "delegation.updated", delegation);
        return ToDto(delegation);
    }

    public async Task<DelegationDto?> HandleDelegationFinishedAsync(string delegationId, string status)
    {
        ValidateRequired(delegationId, nameof(delegationId));
        ValidateTerminalStatus(status);

        var delegation = await delegationRepository.GetByIdAsync(delegationId);
        if (delegation is null)
            return null;

        if (TerminalStatuses.Contains(delegation.Status))
        {
            if (delegation.Status == status)
                return ToDto(delegation);

            throw new InvalidOperationException($"Cannot transition terminal delegation from '{delegation.Status}' to '{status}'.");
        }

        if (delegation.Status != "pending" && delegation.Status != "running")
            throw new InvalidOperationException($"Cannot finish delegation from '{delegation.Status}'.");

        var now = DateTime.UtcNow.ToString("O");
        await delegationRepository.UpdateStatusAsync(delegation.Id, status, now, now);

        delegation.Status = status;
        delegation.UpdatedAt = now;
        delegation.CompletedAt = now;

        await BroadcastAsync(delegation.ParentSessionId, "delegation.updated", delegation);
        return ToDto(delegation);
    }

    public async Task<IReadOnlyList<DelegationDto>> GetDelegationsAsync(string parentSessionId)
    {
        ValidateRequired(parentSessionId, nameof(parentSessionId));

        var delegations = await delegationRepository.GetByParentSessionIdAsync(parentSessionId);
        return delegations.Select(ToDto).ToList();
    }

    private static void ValidateRequired(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Value is required.", paramName);
    }

    private static void ValidateTerminalStatus(string status)
    {
        ValidateRequired(status, nameof(status));
        if (!TerminalStatuses.Contains(status))
            throw new ArgumentException($"Unsupported terminal status '{status}'.", nameof(status));
    }

    private async Task BroadcastAsync(string parentSessionId, string eventType, Delegation delegation)
    {
        await eventBroadcaster.BroadcastAsync(
            $"session:{parentSessionId}",
            eventType,
            new DelegationEventDto(
                delegation.Id,
                delegation.ParentSessionId,
                delegation.ParentToolCallId,
                delegation.ChildSessionId,
                delegation.Title,
                delegation.Status));
    }

    private static DelegationDto ToDto(Delegation delegation) => new(
        delegation.Id,
        delegation.ParentToolCallId,
        delegation.ChildSessionId,
        delegation.Title,
        delegation.Status);
}

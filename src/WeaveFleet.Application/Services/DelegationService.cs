using WeaveFleet.Application.DTOs;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Services;

public sealed class DelegationService(
    IDelegationRepository delegationRepository,
    IEventBroadcaster eventBroadcaster,
    IUserContext userContext,
    SessionActivityWriteService? sessionActivityWriteService)
{
    private static readonly HashSet<string> TerminalStatuses = new(StringComparer.Ordinal)
    {
        "completed",
        "error",
        "cancelled"
    };

    public DelegationService(
        IDelegationRepository delegationRepository,
        IEventBroadcaster eventBroadcaster,
        IUserContext userContext)
        : this(delegationRepository, eventBroadcaster, userContext, sessionActivityWriteService: null)
    {
    }

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

        if (sessionActivityWriteService is null)
        {
            await delegationRepository.InsertAsync(delegation);
            await BroadcastAsync(parentSessionId, "delegation.created", delegation);
        }
        else
        {
            await sessionActivityWriteService.WriteAsync(
                new SessionActivityWriteRequest
                {
                    DelegationsToInsert = [delegation],
                    OutboxMessages = [CreateOutboxMessage(parentSessionId, "delegation.created", delegation, now)]
                },
                CancellationToken.None);
        }

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
        delegation.ChildSessionId = childSessionId;
        delegation.Status = "running";
        delegation.UpdatedAt = now;
        delegation.CompletedAt = null;

        if (sessionActivityWriteService is null)
        {
            if (shouldUpdateChild)
                await delegationRepository.UpdateChildSessionIdAsync(delegation.Id, childSessionId, now);

            if (shouldUpdateStatus)
                await delegationRepository.UpdateStatusAsync(delegation.Id, "running", now, null);

            await BroadcastAsync(parentSessionId, "delegation.updated", delegation);
        }
        else
        {
            await sessionActivityWriteService.WriteAsync(
                new SessionActivityWriteRequest
                {
                    DelegationChildSessionUpdates = shouldUpdateChild
                        ? [new DelegationChildSessionUpdate
                        {
                            Id = delegation.Id,
                            ChildSessionId = childSessionId,
                            UpdatedAt = now
                        }]
                        : [],
                    DelegationStatusUpdates = shouldUpdateStatus
                        ? [new DelegationStatusUpdate
                        {
                            Id = delegation.Id,
                            Status = "running",
                            UpdatedAt = now,
                            CompletedAt = null
                        }]
                        : [],
                    OutboxMessages = [CreateOutboxMessage(parentSessionId, "delegation.updated", delegation, now)]
                },
                CancellationToken.None);
        }

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
        delegation.Status = status;
        delegation.UpdatedAt = now;
        delegation.CompletedAt = now;

        if (sessionActivityWriteService is null)
        {
            await delegationRepository.UpdateStatusAsync(delegation.Id, status, now, now);
            await BroadcastAsync(delegation.ParentSessionId, "delegation.updated", delegation);
        }
        else
        {
            await sessionActivityWriteService.WriteAsync(
                new SessionActivityWriteRequest
                {
                    DelegationStatusUpdates = [new DelegationStatusUpdate
                    {
                        Id = delegation.Id,
                        Status = status,
                        UpdatedAt = now,
                        CompletedAt = now
                    }],
                    OutboxMessages = [CreateOutboxMessage(delegation.ParentSessionId, "delegation.updated", delegation, now)]
                },
                CancellationToken.None);
        }

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

    private OutboxMessage CreateOutboxMessage(string parentSessionId, string eventType, Delegation delegation, string createdAt)
    {
        return new OutboxMessage
        {
            Topic = $"session:{parentSessionId}",
            Type = eventType,
            Payload = MessagePersistenceService.SerializePayload(new DelegationEventDto(
                delegation.Id,
                delegation.ParentSessionId,
                delegation.ParentToolCallId,
                delegation.ChildSessionId,
                delegation.Title,
                delegation.Status,
                delegation.CreatedAt)),
            UserId = userContext.UserId,
            CreatedAt = createdAt,
            AvailableAt = createdAt
        };
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
                delegation.Status,
                delegation.CreatedAt),
            userContext.UserId,
            CancellationToken.None);
    }

    private static DelegationDto ToDto(Delegation delegation) => new(
        delegation.Id,
        delegation.ParentToolCallId,
        delegation.ChildSessionId,
        delegation.Title,
        delegation.Status,
        delegation.CreatedAt);
}

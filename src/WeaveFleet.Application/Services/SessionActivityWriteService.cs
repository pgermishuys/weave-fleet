using WeaveFleet.Application.Data;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Services;

/// <summary>
/// Persists durable session activity and outbox rows in one database transaction.
/// </summary>
public sealed class SessionActivityWriteService(
    IDbConnectionFactory connectionFactory,
    IMessageRepository messageRepository,
    IDelegationRepository delegationRepository,
    ISessionRepository sessionRepository,
    ISmartLinkRepository smartLinkRepository,
    IOutboxRepository outboxRepository,
    IOutboxDispatcher outboxDispatcher)
{
    public async Task<SessionActivityWriteResult> WriteAsync(
        SessionActivityWriteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var connection = connectionFactory.CreateConnection();
        using var transaction = connection.BeginTransaction();
        var outboxSequenceNumbers = new List<long>(request.OutboxMessages.Count);

        try
        {
            foreach (var session in request.SessionsToInsert)
                await sessionRepository.InsertAsync(connection, transaction, session).ConfigureAwait(false);

            foreach (var update in request.SessionStatusUpdates)
                await sessionRepository.UpdateStatusAsync(connection, transaction, update.Id, update.Status, update.StoppedAt).ConfigureAwait(false);

            foreach (var archive in request.SessionArchives)
                await sessionRepository.ArchiveAsync(connection, transaction, archive.SessionId, archive.ArchivedAt).ConfigureAwait(false);

            foreach (var sessionId in request.SessionUnarchives)
                await sessionRepository.UnarchiveAsync(connection, transaction, sessionId).ConfigureAwait(false);

            foreach (var message in request.MessagesToUpsert)
                await messageRepository.UpsertAsync(connection, transaction, message).ConfigureAwait(false);

            if (request.MessageBatchToUpsert.Count > 0)
                await messageRepository.UpsertBatchAsync(connection, transaction, request.MessageBatchToUpsert).ConfigureAwait(false);

            foreach (var delegation in request.DelegationsToInsert)
                await delegationRepository.InsertAsync(connection, transaction, delegation).ConfigureAwait(false);

            foreach (var update in request.DelegationStatusUpdates)
            {
                await delegationRepository.UpdateStatusAsync(
                    connection,
                    transaction,
                    update.Id,
                    update.Status,
                    update.UpdatedAt,
                    update.CompletedAt).ConfigureAwait(false);
            }

            foreach (var update in request.DelegationChildSessionUpdates)
            {
                await delegationRepository.UpdateChildSessionIdAsync(
                    connection,
                    transaction,
                    update.Id,
                    update.ChildSessionId,
                    update.UpdatedAt).ConfigureAwait(false);
            }

            foreach (var parentSessionId in request.DelegationDeletesByParentSessionId)
                await delegationRepository.DeleteByParentSessionIdAsync(connection, transaction, parentSessionId).ConfigureAwait(false);

            foreach (var sessionId in request.SmartLinkDeletesBySessionId)
                await smartLinkRepository.DeleteBySessionIdAsync(connection, transaction, sessionId).ConfigureAwait(false);

            foreach (var sessionId in request.SessionDeletes)
                await sessionRepository.DeleteAsync(connection, transaction, sessionId).ConfigureAwait(false);

            foreach (var outboxMessage in request.OutboxMessages)
            {
                var sequenceNumber = await outboxRepository.EnqueueAsync(connection, transaction, outboxMessage).ConfigureAwait(false);
                outboxSequenceNumbers.Add(sequenceNumber);
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }

        if (outboxSequenceNumbers.Count > 0)
            await outboxDispatcher.NotifyNewMessagesAsync(cancellationToken).ConfigureAwait(false);

        return new SessionActivityWriteResult(outboxSequenceNumbers);
    }
}

public sealed class SessionActivityWriteRequest
{
    public IReadOnlyList<Session> SessionsToInsert { get; init; } = [];
    public IReadOnlyList<SessionStatusUpdate> SessionStatusUpdates { get; init; } = [];
    public IReadOnlyList<SessionArchiveUpdate> SessionArchives { get; init; } = [];
    public IReadOnlyList<string> SessionUnarchives { get; init; } = [];
    public IReadOnlyList<string> SessionDeletes { get; init; } = [];
    public IReadOnlyList<PersistedMessage> MessagesToUpsert { get; init; } = [];
    public IReadOnlyList<PersistedMessage> MessageBatchToUpsert { get; init; } = [];
    public IReadOnlyList<Delegation> DelegationsToInsert { get; init; } = [];
    public IReadOnlyList<DelegationStatusUpdate> DelegationStatusUpdates { get; init; } = [];
    public IReadOnlyList<DelegationChildSessionUpdate> DelegationChildSessionUpdates { get; init; } = [];
    public IReadOnlyList<string> DelegationDeletesByParentSessionId { get; init; } = [];
    public IReadOnlyList<string> SmartLinkDeletesBySessionId { get; init; } = [];
    public IReadOnlyList<OutboxMessage> OutboxMessages { get; init; } = [];
}

public sealed class SessionStatusUpdate
{
    public string Id { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string? StoppedAt { get; init; }
}

public sealed class SessionArchiveUpdate
{
    public string SessionId { get; init; } = string.Empty;
    public string ArchivedAt { get; init; } = string.Empty;
}

public sealed class DelegationStatusUpdate
{
    public string Id { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string UpdatedAt { get; init; } = string.Empty;
    public string? CompletedAt { get; init; }
}

public sealed class DelegationChildSessionUpdate
{
    public string Id { get; init; } = string.Empty;
    public string? ChildSessionId { get; init; }
    public string UpdatedAt { get; init; } = string.Empty;
}

public sealed class SessionActivityWriteResult(IReadOnlyList<long> outboxSequenceNumbers)
{
    public IReadOnlyList<long> OutboxSequenceNumbers { get; } = outboxSequenceNumbers;
}

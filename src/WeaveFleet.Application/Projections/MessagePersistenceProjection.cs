using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Application.Projections;

/// <summary>
/// Durable projection that writes every harness event to the SQLite read model via
/// <see cref="IHarnessEventPersister"/>. The persister emits outbox entries as well, which the
/// existing outbox dispatcher fans out to WebSocket subscribers.
/// </summary>
public sealed class MessagePersistenceProjection : IProjection<HarnessEvent>
{
    private readonly IHarnessEventPersister _persister;
    public MessagePersistenceProjection(IHarnessEventPersister persister) => _persister = persister;

    public string Name => "message-persistence";

    public Task HandleAsync(HarnessEvent evt, ProjectionContext ctx, CancellationToken ct)
    {
        if (ctx.UserId is null) return Task.CompletedTask;
        return _persister.HandleAsync(ctx.FleetSessionId, ctx.UserId, evt, ct);
    }
}

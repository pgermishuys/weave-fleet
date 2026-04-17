namespace WeaveFleet.Application.Projections;

/// <summary>
/// A projection consumes events of type <typeparamref name="T"/> from the event substrate.
/// Implementations should be idempotent — JetStream may redeliver a message on transient failure.
/// </summary>
public interface IProjection<T>
{
    /// <summary>Stable projection name. Used for consumer durable name and metric labels.</summary>
    string Name { get; }

    Task HandleAsync(T evt, ProjectionContext ctx, CancellationToken ct);
}

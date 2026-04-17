namespace WeaveFleet.Application.Projections;

/// <summary>
/// Metadata extracted from a NATS message and handed to a projection.
/// Subject parts are parsed by <c>NatsNamingStrategy.ParseDurableSubject</c>; header values are
/// read from the message's headers collection.
/// </summary>
public readonly record struct ProjectionContext(
    string Tenant,
    string ProjectId,
    string FleetSessionId,
    string EventType,
    string? UserId,
    string? HarnessType,
    long StreamSequence);

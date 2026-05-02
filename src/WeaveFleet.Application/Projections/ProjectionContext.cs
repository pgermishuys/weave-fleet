namespace WeaveFleet.Application.Projections;

/// <summary>
/// Metadata passed to a projection when an event is dispatched.
/// </summary>
public readonly record struct ProjectionContext(
    string Tenant,
    string ProjectId,
    string FleetSessionId,
    string EventType,
    string? UserId,
    string? HarnessType,
    long StreamSequence,
    long PublishSequence);

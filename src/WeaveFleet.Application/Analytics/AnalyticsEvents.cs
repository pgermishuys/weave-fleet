namespace WeaveFleet.Application.Analytics;

/// <summary>Token/cost event data extracted from a single assistant SSE message.</summary>
public sealed record TokenEventData(
    string EventId,
    string SessionId,
    string? ProjectId,
    string? ProjectName,
    string? WorkspaceDirectory,
    string? ModelId,
    string? ProviderId,
    double TokensInput,
    double TokensOutput,
    double TokensReasoning,
    double TokensCacheRead,
    double TokensCacheWrite,
    double TokensTotal,
    double Cost,
    double? EstimatedCost,
    DateTimeOffset CreatedAt,
    string UserId = "local-user");

/// <summary>Session-level snapshot data emitted on session create and stop.</summary>
public sealed record SessionSnapshotData(
    string SessionId,
    string? ParentSessionId,
    string? ProjectId,
    string? ProjectName,
    string? WorkspaceDirectory,
    string? Title,
    string? Status,
    double TotalTokens,
    double TotalCost,
    double TotalEstimatedCost,
    int MessageCount,
    List<string> ModelIds,
    DateTimeOffset CreatedAt,
    DateTimeOffset? EndedAt,
    double? DurationSeconds,
    string UserId = "local-user");

/// <summary>Discriminated union envelope for the analytics channel.</summary>
public abstract record AnalyticsEventEnvelope;

/// <summary>Wraps a <see cref="TokenEventData"/> for channel delivery.</summary>
public sealed record TokenEventEnvelope(TokenEventData Data) : AnalyticsEventEnvelope;

/// <summary>Wraps a <see cref="SessionSnapshotData"/> for channel delivery.</summary>
public sealed record SessionSnapshotEnvelope(SessionSnapshotData Data) : AnalyticsEventEnvelope;

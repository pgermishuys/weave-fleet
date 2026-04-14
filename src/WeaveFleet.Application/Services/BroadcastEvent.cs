using System.Text.Json;

namespace WeaveFleet.Application.Services;

/// <summary>A broadcast event published to subscribers.</summary>
public sealed record BroadcastEvent(
    string Topic,
    string Type,
    JsonElement Payload,
    DateTimeOffset Timestamp,
    long? SequenceNumber = null,
    /// <summary>
    /// The user ID that owns this event. Subscribers receive only events whose
    /// <see cref="UserId"/> matches their own, or events with a null UserId (system-level).
    /// Track 3 will extend delivery to session participants.
    /// </summary>
    string? UserId = null);

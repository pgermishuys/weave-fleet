using System.Text.Json;

namespace WeaveFleet.Application.Services;

/// <summary>A broadcast event published to subscribers.</summary>
public sealed record BroadcastEvent(
    string Topic,
    string Type,
    JsonElement Payload,
    DateTimeOffset Timestamp);

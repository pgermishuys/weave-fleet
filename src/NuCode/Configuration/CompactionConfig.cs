using System.Text.Json.Serialization;

namespace NuCode.Configuration;

/// <summary>
/// Configuration for conversation compaction behaviour.
/// </summary>
public sealed class CompactionConfig
{
    [JsonPropertyName("auto")]
    public bool? Auto { get; init; }

    [JsonPropertyName("prune")]
    public bool? Prune { get; init; }

    [JsonPropertyName("reserved")]
    public int? Reserved { get; init; }

    /// <summary>Number of messages before proactive compaction triggers (default 50).</summary>
    [JsonPropertyName("messageThreshold")]
    public int? MessageThreshold { get; init; }

    /// <summary>Estimated token count threshold before compaction triggers.</summary>
    [JsonPropertyName("tokenThreshold")]
    public int? TokenThreshold { get; init; }

    /// <summary>Number of recent messages to preserve after compaction (default 10).</summary>
    [JsonPropertyName("recentMessagesToKeep")]
    public int? RecentMessagesToKeep { get; init; }
}

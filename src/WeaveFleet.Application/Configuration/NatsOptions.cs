namespace WeaveFleet.Application.Configuration;

/// <summary>
/// NATS event-substrate configuration. See docs/nats-event-substrate-design.md.
/// </summary>
public sealed class NatsOptions
{
    /// <summary>URL of an external NATS broker. When null, Fleet launches its bundled nats-server.</summary>
    public string? ExternalUrl { get; set; }

    /// <summary>Path to a NATS creds file. Only honoured when <see cref="ExternalUrl"/> is set.</summary>
    public string? CredsFile { get; set; }

    /// <summary>Directory for embedded nats-server JetStream file storage. Default: ./data/nats.</summary>
    public string DataDirectory { get; set; } = "./data/nats";

    /// <summary>Durable JetStream stream name. Default: fleet-sessions.</summary>
    public string StreamName { get; set; } = "fleet-sessions";

    /// <summary>MaxAge safety net for stream retention. Default: 24h (local dev).</summary>
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromHours(24);

    /// <summary>MaxBytes for the durable stream. -1 = unlimited.</summary>
    public long MaxBytes { get; set; } = -1;

    /// <summary>
    /// Maximum accepted payload size in bytes for both publish and consume paths. Default 4 MiB.
    /// Consumers TERM messages larger than this rather than risking OOM.
    /// </summary>
    public int MaxPayloadBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>Tenant/prefix string for subject construction. Default: tenant.default.</summary>
    public string TenantPrefix { get; set; } = "tenant.default";

    /// <summary>
    /// Stable identifier for this Fleet node. Used to suffix per-node durable consumer names
    /// (e.g. WebSocket fan-out). Defaults to <see cref="Environment.MachineName"/>.
    /// </summary>
    public string NodeId { get; set; } = Environment.MachineName;

    /// <summary>
    /// Maximum JetStream redelivery attempts before a projection TERMs a message as poison. Default: 5.
    /// </summary>
    public int ProjectionRetryBudget { get; set; } = 5;
}

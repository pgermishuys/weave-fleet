namespace WeaveFleet.Application.Configuration;

/// <summary>
/// Selects which event-bus transport Fleet uses internally.
/// </summary>
public enum TransportKind
{
    /// <summary>
    /// Pure in-process bus backed by <c>System.Threading.Channels</c> and SQLite.
    /// No child process is spawned. Suitable for single-node local development.
    /// This is the default.
    /// </summary>
    InProcess,

    /// <summary>
    /// NATS-backed bus (embedded nats-server child process or external broker).
    /// Required for multi-node / cross-process scenarios.
    /// </summary>
    Nats,
}

/// <summary>
/// Top-level event bus configuration. Controls which transport is active.
/// </summary>
public sealed class EventBusOptions
{
    /// <summary>
    /// Selects the event-bus transport. Default: <see cref="TransportKind.InProcess"/>.
    /// </summary>
    public TransportKind Transport { get; set; } = TransportKind.InProcess;
}

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode.Pooling;

/// <summary>
/// Provides an operator-facing snapshot of the pooled OpenCode harness state.
/// </summary>
public interface IOpenCodePoolHealthCheck
{
    OpenCodePoolHealthStatus GetStatus();
}

/// <summary>
/// Reads pooled OpenCode harness health from the singleton OpenCode runtime.
/// </summary>
public sealed class PoolHealthCheck(OpenCodeHarnessRuntime runtime) : IOpenCodePoolHealthCheck
{
    public OpenCodePoolHealthStatus GetStatus() => runtime.GetPooledOpenCodePoolHealth();
}

public sealed record OpenCodePoolHealthStatus(
    int InstanceCount,
    int SessionCount,
    int WarmCount,
    int ActiveCount,
    IReadOnlyList<OpenCodePoolInstanceHealth> Instances);

/// <summary>
/// Operator-facing health snapshot for a single pooled OpenCode process instance.
/// All identifying information is replaced with safe opaque fingerprints —
/// no raw owner identifiers, credential hashes, or resume tokens are included.
/// </summary>
public sealed record OpenCodePoolInstanceHealth(
    string InstanceId,
    int SessionCount,
    int? ProcessId,
    bool IsAvailable,
    bool IsFaulted,
    bool IsDisposed,
    /// <summary>
    /// A short opaque fingerprint derived from the composite pool key (owner + credential hash).
    /// Safe to log and expose to operators; does not reveal owner identity or credential material.
    /// </summary>
    string PartitionFingerprint,
    /// <summary>
    /// <see langword="true"/> when the instance is warm-held (idle, ref-count = 0) but not yet
    /// shut down by the idle-TTL timer. <see langword="false"/> when at least one active lease is held.
    /// </summary>
    bool IsWarm);

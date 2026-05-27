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
    IReadOnlyList<OpenCodePoolInstanceHealth> Instances);

public sealed record OpenCodePoolInstanceHealth(
    string InstanceId,
    int SessionCount,
    int? ProcessId,
    bool IsAvailable,
    bool IsFaulted,
    bool IsDisposed);

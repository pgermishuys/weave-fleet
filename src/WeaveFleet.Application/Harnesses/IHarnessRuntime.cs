using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Application.Harnesses;

/// <summary>
/// Provisioning and lifecycle surface for a harness type.
/// Handles availability checks, runtime preparation, and spawning/resuming sessions.
/// One instance per harness type is registered in DI.
/// </summary>
public interface IHarnessRuntime
{
    /// <summary>Machine-readable harness type identifier, e.g. "opencode", "claude-code".</summary>
    string HarnessType { get; }

    /// <summary>Check whether this harness can be used (binary found, auth configured, etc.).</summary>
    Task<HarnessAvailability> CheckAvailabilityAsync(CancellationToken ct);

    /// <summary>
    /// Prepare the runtime for this session.
    /// The harness internally resolves credential requirements, validates availability,
    /// and materialises runtime artifacts (env vars, config files, etc.).
    /// The orchestrator never inspects <see cref="RuntimeLaunchArtifacts"/> contents —
    /// it only checks readiness and forwards artifacts to spawn/resume options.
    /// </summary>
    Task<RuntimePreparation> PrepareRuntimeAsync(RuntimePreparationContext context, CancellationToken ct);

    /// <summary>Spawn a new agent instance for the given session.</summary>
    Task<IHarnessSession> SpawnAsync(HarnessSpawnOptions options, CancellationToken ct);

    /// <summary>Resume an existing agent session using the stored resume token.</summary>
    Task<IHarnessSession> ResumeAsync(HarnessResumeOptions options, CancellationToken ct);
}

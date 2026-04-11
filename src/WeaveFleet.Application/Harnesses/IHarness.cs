using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Application.Harnesses;

/// <summary>
/// Factory and metadata for a harness type.
/// One instance per harness type is registered in DI.
/// </summary>
public interface IHarness
{
    /// <summary>Machine-readable type identifier, e.g. "opencode", "claude-code".</summary>
    string Type { get; }

    /// <summary>Human-readable display name, e.g. "OpenCode", "Claude Code".</summary>
    string DisplayName { get; }

    /// <summary>Declares what this harness supports.</summary>
    HarnessCapabilities Capabilities { get; }

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
    Task<IHarnessInstance> SpawnAsync(HarnessSpawnOptions options, CancellationToken ct);

    /// <summary>Resume an existing agent session using the stored resume token.</summary>
    Task<IHarnessInstance> ResumeAsync(HarnessResumeOptions options, CancellationToken ct);
}

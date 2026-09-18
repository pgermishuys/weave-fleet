using WeaveFleet.Domain.Entities;
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
    /// How to install this harness or sign in to it on this machine, given what <see cref="CheckAvailabilityAsync"/>
    /// found. <see langword="null"/> when Fleet can't help set it up.
    /// </summary>
    HarnessSetup? GetSetup(HarnessAvailability availability) => null;

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

    /// <summary>
    /// Warms up a pooled instance for the specified owner by loading server-side preferences
    /// and decrypted credentials. The owner identity must be resolved server-side — callers
    /// must never supply caller-controlled owner IDs, credential hashes, resume tokens, or
    /// working directories.
    /// Returns <c>true</c> when warmup was attempted, <c>false</c> when this runtime does not
    /// support pooled warmup or warmup was skipped (e.g. pooled mode disabled for the user).
    /// </summary>
    Task<bool> WarmupPooledInstanceAsync(string ownerUserId, CancellationToken ct);

    /// <summary>
    /// Tries a profile's content the way a session would use it, so a broken profile is caught before it's saved.
    /// Only harnesses that declare <see cref="HarnessCapabilities.SupportsProfiles"/> are asked.
    /// </summary>
    Task<HarnessProfileCheck> CheckProfileAsync(string ownerUserId, string content, CancellationToken ct) =>
        Task.FromResult(HarnessProfileCheck.Passed);

    /// <summary>
    /// Lists the agents and models this harness offers in <paramref name="directory"/> on <paramref name="profile"/>
    /// (null for none) without a session, so a new session can start with a chosen one. Null when the harness can't
    /// list them that way; the new-session composer then offers no choice. <paramref name="directory"/> must already
    /// be validated by the caller.
    /// </summary>
    Task<HarnessCatalog?> GetCatalogAsync(string ownerUserId, string directory, HarnessProfile? profile, CancellationToken ct)
        => Task.FromResult<HarnessCatalog?>(null);
}

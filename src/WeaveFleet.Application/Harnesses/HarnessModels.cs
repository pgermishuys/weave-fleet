using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Application.Harnesses;

/// <summary>Options for resuming an existing harness session.</summary>
public sealed record HarnessResumeOptions
{
    public required string SessionId { get; init; }
    public required string WorkingDirectory { get; init; }
    public required string ResumeToken { get; init; }
    public required string OwnerUserId { get; init; }
    public string? ProjectId { get; init; }
    public string? ProjectName { get; init; }

    /// <summary>
    /// Opaque launch artifacts produced by <see cref="IHarnessRuntime.PrepareRuntimeAsync"/>.
    /// Passed through from the orchestrator without inspection.
    /// Each harness implementation casts this to its own internal subclass in <c>ResumeAsync</c>.
    /// Null in local mode (no cloud credentials required).
    /// </summary>
    public RuntimeLaunchArtifacts? LaunchArtifacts { get; init; }
}

/// <summary>Options for spawning a new harness instance.</summary>
public sealed record HarnessSpawnOptions
{
    public required string SessionId { get; init; }
    public required string WorkingDirectory { get; init; }
    public required string OwnerUserId { get; init; }
    public string? InitialPrompt { get; init; }
    public string? Branch { get; init; }
    public string? ProjectId { get; init; }
    public string? ProjectName { get; init; }

    /// <summary>
    /// Optional scenario id for the beta-tester rig. Production harnesses ignore this; the
    /// in-process test harness uses it to look up a scripted scenario JSON file at
    /// <c>tests/beta-harness/.runtime/scenarios/{ScenarioId}.json</c>. When null the test
    /// harness falls back to a deterministic echo response.
    /// </summary>
    public string? ScenarioId { get; init; }

    /// <summary>
    /// Opaque launch artifacts produced by <see cref="IHarnessRuntime.PrepareRuntimeAsync"/>.
    /// Passed through from the orchestrator without inspection.
    /// Each harness implementation casts this to its own internal subclass in <c>SpawnAsync</c>.
    /// Null in local mode (no cloud credentials required).
    /// </summary>
    public RuntimeLaunchArtifacts? LaunchArtifacts { get; init; }
}

/// <summary>
/// API-facing DTO returned by GET /api/harnesses.
/// Combines harness metadata with runtime availability.
/// </summary>
public sealed record HarnessInfo(
    string Type,
    string DisplayName,
    bool Available,
    string? Reason,
    HarnessCapabilities Capabilities);

/// <summary>An agent persona exposed by a harness.</summary>
public sealed record HarnessAgent(string Name, string? Description, string? Mode);

/// <summary>An AI provider supported by a harness.</summary>
public sealed record HarnessProvider(string Id, string Name, IReadOnlyList<HarnessModel> Models);

/// <summary>An AI model within a provider.</summary>
public sealed record HarnessModel(string Id, string Name);

// ── Runtime preparation ─────────────────────────────────────────────────────

/// <summary>
/// Input context provided to <see cref="IHarnessRuntime.PrepareRuntimeAsync"/>.
/// The harness uses this to internally resolve, validate, and materialise credentials
/// into runtime launch artifacts. The orchestrator never inspects the contents.
/// </summary>
public sealed record RuntimePreparationContext
{
    /// <summary>Session owner's user identifier.</summary>
    public required string UserId { get; init; }

    /// <summary>
    /// Full credential bag for the user — opaque to the orchestrator.
    /// The harness performs all requirement resolution, selection, and validation internally.
    /// </summary>
    public required IReadOnlyList<UserCredential> UserCredentials { get; init; }

    /// <summary>Optional model ID from the session request (null if not known at preparation time).</summary>
    public string? ModelId { get; init; }

    /// <summary>Working directory for the session.</summary>
    public required string WorkingDirectory { get; init; }
}

/// <summary>
/// Opaque launch artifacts produced by a successful <see cref="IHarnessRuntime.PrepareRuntimeAsync"/>.
/// The orchestrator passes this through to <see cref="HarnessSpawnOptions"/>/<see cref="HarnessResumeOptions"/>
/// without reading or interpreting its contents.
/// Each harness implementation subclasses this to carry its own runtime data (env vars, config file paths, etc.).
/// </summary>
public abstract record RuntimeLaunchArtifacts;

/// <summary>
/// A product-level validation error returned when the harness cannot proceed (e.g. missing credentials).
/// Never contains harness-internal details such as env var names, namespace/kind identifiers, or runtime mechanisms.
/// </summary>
public sealed record RuntimePreparationError(
    /// <summary>Machine-readable code, e.g. "MissingCredential".</summary>
    string Code,
    /// <summary>User-facing message, e.g. "An Anthropic API key is required to use this model."</summary>
    string Message,
    /// <summary>Optional actionable guidance, e.g. "Add an API key in Settings → Credentials".</summary>
    string? Guidance = null);

/// <summary>
/// Discriminated result of <see cref="IHarnessRuntime.PrepareRuntimeAsync"/>.
/// Either <see cref="Ready"/> (with opaque launch artifacts) or <see cref="NotReady"/> (with product-level errors).
/// </summary>
public abstract record RuntimePreparation
{
    private RuntimePreparation() { }

    /// <summary>The harness is ready to launch. Artifacts are passed through to spawn/resume options.</summary>
    public sealed record Ready(RuntimeLaunchArtifacts Artifacts) : RuntimePreparation;

    /// <summary>The harness cannot launch. Errors are product-level and safe to surface to the user.</summary>
    public sealed record NotReady(IReadOnlyList<RuntimePreparationError> Errors) : RuntimePreparation;
}

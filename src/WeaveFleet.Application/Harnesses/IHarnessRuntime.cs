using WeaveFleet.Application.Weave;
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

    /// <summary>The npm package whose latest version is this harness's latest release; <see langword="null"/> when Fleet can't tell.</summary>
    string? LatestVersionPackage => null;

    /// <summary>The oldest version Fleet works with. An older install is <see cref="HarnessStates.UpdateNeeded"/>.</summary>
    string? MinimumVersion => null;

    /// <summary>
    /// The command that updates this install to <paramref name="version"/> (the latest when <see langword="null"/>),
    /// run by Fleet without a terminal. <see langword="null"/> when Fleet can't update it.
    /// </summary>
    HarnessCommand? GetUpdateCommand(HarnessAvailability availability, string? version) => null;

    /// <summary>Called after an update succeeded, e.g. to restart idle processes on the new version.</summary>
    Task AfterUpdateAsync(CancellationToken ct) => Task.CompletedTask;

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

    /// <summary>
    /// Starts a conversation off the record with no session behind it, in <paramref name="options"/>' folder and on its
    /// model: nothing is kept afterwards. Null when the harness can't ask that way. The folder must already be
    /// validated by the caller. Used to draft a workflow from a description.
    /// </summary>
    Task<IOffTheRecordConversation?> StartOffTheRecordAsync(OffTheRecordOptions options, CancellationToken ct)
        => Task.FromResult<IOffTheRecordConversation?>(null);

    /// <summary>
    /// The harness's own provider sign-ins, for harnesses that declare
    /// <see cref="HarnessCapabilities.SupportsProviderSignIn"/>; <see langword="null"/> for the rest.
    /// </summary>
    IHarnessProviderSignIn? ProviderSignIn => null;

    /// <summary>
    /// The owner turned one of Fleet's built-in skills on or off. Sessions they start afterwards should get the change;
    /// a harness that reads the choice when it starts a session or a process needs to do nothing.
    /// </summary>
    Task BuiltInSkillsChangedAsync(string ownerUserId, CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// The Weave plugins this harness loads for the owner, and whether each reads the folder Fleet points it at.
    /// Null when Fleet can't hand this harness a Weave config.
    /// </summary>
    Task<IReadOnlyList<WeaveInstall>?> DetectWeaveAsync(string ownerUserId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<WeaveInstall>?>(null);

    /// <summary>
    /// Tries a draft Weave config (files by their path in Fleet's folder) the way a session would load it, and says
    /// which <paramref name="flavor"/> agents it got. Null when this harness can't try one.
    /// </summary>
    Task<WeaveCheck?> CheckWeaveConfigAsync(
        string ownerUserId,
        WeaveFlavor flavor,
        IReadOnlyDictionary<string, string> files,
        CancellationToken ct) =>
        Task.FromResult<WeaveCheck?>(null);

    /// <summary>
    /// The owner saved their Weave config. New processes get it when they start; a harness that keeps processes
    /// running hands it to them too, without interrupting a running turn.
    /// </summary>
    Task WeaveConfigChangedAsync(string ownerUserId, CancellationToken ct) => Task.CompletedTask;

    /// <summary>How far the owner's last save has got in running processes; null when there's nothing to report.</summary>
    WeaveApplyStatus? GetWeaveApplyStatus(string ownerUserId) => null;
}

/// <summary>Where and on what a session-less conversation off the record runs.</summary>
/// <param name="ProviderId">With <paramref name="ModelId"/>, the model; both null for the harness's default.</param>
/// <param name="Variant">The model's reasoning effort, if any.</param>
public sealed record OffTheRecordOptions(
    string OwnerUserId,
    string Directory,
    HarnessProfile? Profile,
    string? ProviderId,
    string? ModelId,
    string? Variant);

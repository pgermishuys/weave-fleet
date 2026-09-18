using WeaveFleet.Application.Harnesses;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Testing.Fakes;

public sealed class FakeHarnessRuntime : IHarnessRuntime
{
    public FakeHarnessRuntime(string harnessType = "opencode", bool available = true, string? availabilityReason = null)
    {
        HarnessType = harnessType;
        Available = available;
        AvailabilityReason = availabilityReason;
    }

    // ── Configurable properties ──────────────────────────────────────────────

    public string HarnessType { get; set; }
    public bool Available { get; set; }
    public string? AvailabilityReason { get; set; }

    /// <summary>When set, returned by <see cref="CheckAvailabilityAsync"/> instead of one built from <see cref="Available"/>.</summary>
    public HarnessAvailability? Availability { get; set; }

    /// <summary>
    /// Configurable preparation result. Defaults to <see cref="RuntimePreparation.Ready"/> with a no-op artifact.
    /// </summary>
    public RuntimePreparation? PreparationResult { get; set; }

    /// <summary>
    /// Default session returned by <see cref="SpawnAsync"/> and <see cref="ResumeAsync"/> when no behavior is configured.
    /// </summary>
    public IHarnessSession DefaultSession { get; set; } = new FakeHarnessSession("inst-1");

    // ── Configurable behaviors ───────────────────────────────────────────────

    public Func<HarnessSpawnOptions, CancellationToken, Task<IHarnessSession>>? SpawnBehavior { get; set; }
    public Func<HarnessResumeOptions, CancellationToken, Task<IHarnessSession>>? ResumeBehavior { get; set; }

    /// <summary>
    /// Optional override for <see cref="PrepareRuntimeAsync"/>. When set, called instead of returning <see cref="PreparationResult"/>.
    /// Supports capturing the <see cref="RuntimePreparationContext"/> argument for assertions.
    /// </summary>
    public Func<RuntimePreparationContext, CancellationToken, Task<RuntimePreparation>>? PrepareRuntimeBehavior { get; set; }

    // ── Call-tracking for assertions ─────────────────────────────────────────

    public List<HarnessSpawnOptions> SpawnCalls { get; } = [];
    public List<HarnessResumeOptions> ResumeCalls { get; } = [];

    // ── IHarnessRuntime ──────────────────────────────────────────────────────

    public Task<HarnessAvailability> CheckAvailabilityAsync(CancellationToken ct)
        => Task.FromResult(Availability ?? new HarnessAvailability(Available, AvailabilityReason));

    /// <summary>Returned by <see cref="GetSetup"/>.</summary>
    public HarnessSetup? Setup { get; set; }

    public HarnessSetup? GetSetup(HarnessAvailability availability) => Setup;

    public Task<RuntimePreparation> PrepareRuntimeAsync(RuntimePreparationContext context, CancellationToken ct)
    {
        PrepareCalls.Add(context);
        if (PrepareRuntimeBehavior is not null)
            return PrepareRuntimeBehavior(context, ct);
        var result = PreparationResult ?? new RuntimePreparation.Ready(new FakeRuntimeLaunchArtifacts());
        return Task.FromResult(result);
    }

    public Task<IHarnessSession> SpawnAsync(HarnessSpawnOptions options, CancellationToken ct)
    {
        SpawnCalls.Add(options);
        return SpawnBehavior?.Invoke(options, ct)
               ?? Task.FromResult<IHarnessSession>(DefaultSession);
    }

    public Task<IHarnessSession> ResumeAsync(HarnessResumeOptions options, CancellationToken ct)
    {
        ResumeCalls.Add(options);
        return ResumeBehavior?.Invoke(options, ct)
               ?? Task.FromResult<IHarnessSession>(DefaultSession);
    }

    public Task<bool> WarmupPooledInstanceAsync(string ownerUserId, CancellationToken ct)
    {
        WarmupCalls.Add(ownerUserId);
        return Task.FromResult(WarmupResult);
    }

    // ── Call-tracking for assertions (additional) ────────────────────────────

    public List<string> WarmupCalls { get; } = [];

    /// <summary>Value returned by <see cref="WarmupPooledInstanceAsync"/>. Default: false.</summary>
    public bool WarmupResult { get; set; }

    /// <summary>Every context <see cref="PrepareRuntimeAsync"/> was called with, in order.</summary>
    public List<RuntimePreparationContext> PrepareCalls { get; } = [];

    /// <summary>What <see cref="CheckProfileAsync"/> answers. Default: the profile works.</summary>
    public HarnessProfileCheck ProfileCheckResult { get; set; } = HarnessProfileCheck.Passed;

    /// <summary>The content of every profile <see cref="CheckProfileAsync"/> was asked about.</summary>
    public List<string> ProfileChecks { get; } = [];

    public Task<HarnessProfileCheck> CheckProfileAsync(string ownerUserId, string content, CancellationToken ct)
    {
        ProfileChecks.Add(content);
        return Task.FromResult(ProfileCheckResult);
    }

    /// <summary>Answers <see cref="GetCatalogAsync"/>; null (the default) means the harness can't list one.</summary>
    public Func<string, string, CancellationToken, Task<HarnessCatalog?>>? CatalogBehavior { get; set; }

    /// <summary>The owner, directory and profile id (null for none) <see cref="GetCatalogAsync"/> was asked for.</summary>
    public List<(string OwnerUserId, string Directory, string? ProfileId)> CatalogCalls { get; } = [];

    public Task<HarnessCatalog?> GetCatalogAsync(string ownerUserId, string directory, HarnessProfile? profile, CancellationToken ct)
    {
        CatalogCalls.Add((ownerUserId, directory, profile?.Id));
        return CatalogBehavior?.Invoke(ownerUserId, directory, ct) ?? Task.FromResult<HarnessCatalog?>(null);
    }

    private sealed record FakeRuntimeLaunchArtifacts : RuntimeLaunchArtifacts;
}

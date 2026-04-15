using WeaveFleet.Application.Harnesses;
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

    /// <summary>
    /// Configurable preparation result. Defaults to <see cref="RuntimePreparation.Ready"/> with a no-op artifact.
    /// </summary>
    public RuntimePreparation? PreparationResult { get; set; }

    /// <summary>
    /// Default session returned by <see cref="SpawnAsync"/> and <see cref="ResumeAsync"/> when no behavior is configured.
    /// </summary>
    public FakeHarnessSession DefaultSession { get; set; } = new("inst-1");

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
        => Task.FromResult(new HarnessAvailability(Available, AvailabilityReason));

    public Task<RuntimePreparation> PrepareRuntimeAsync(RuntimePreparationContext context, CancellationToken ct)
    {
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

    private sealed record FakeRuntimeLaunchArtifacts : RuntimeLaunchArtifacts;
}

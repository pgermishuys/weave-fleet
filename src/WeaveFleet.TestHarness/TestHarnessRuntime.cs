using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.TestHarness;

/// <summary>
/// A mock <see cref="IHarnessRuntime"/> for E2E and integration tests.
/// Paired with <see cref="TestHarness"/> which provides the descriptor.
/// </summary>
public sealed class TestHarnessRuntime : IHarnessRuntime
{
    private TestScenario _scenario = new();
    private IServiceScopeFactory? _scopeFactory;

    // ── IHarnessRuntime ──────────────────────────────────────────────────────

    /// <inheritdoc/>
    public string HarnessType => "opencode";

    /// <inheritdoc/>
    public Task<HarnessAvailability> CheckAvailabilityAsync(CancellationToken ct)
        => Task.FromResult(new HarnessAvailability(Available: true, Reason: null));

    /// <inheritdoc/>
    public Task<RuntimePreparation> PrepareRuntimeAsync(RuntimePreparationContext context, CancellationToken ct)
        => Task.FromResult<RuntimePreparation>(new RuntimePreparation.Ready(new TestLaunchArtifacts()));

    /// <inheritdoc/>
    public Task<IHarnessSession> SpawnAsync(HarnessSpawnOptions options, CancellationToken ct)
    {
        var scenario = ResolveScenario(options.ScenarioId);
        if (scenario.ThrowOnSpawn)
            throw new InvalidOperationException("TestHarness: configured to fail on spawn.");

        IHarnessSession instance = new TestHarnessSession(
            instanceId: options.SessionId,
            scenario: scenario,
            fleetSessionId: options.SessionId,
            scopeFactory: _scopeFactory,
            ownerUserId: options.OwnerUserId);

        return Task.FromResult(instance);
    }

    /// <inheritdoc/>
    public Task<IHarnessSession> ResumeAsync(HarnessResumeOptions options, CancellationToken ct)
    {
        // Resume reuses the in-memory programmed scenario; file-driven scenarios are looked up
        // by id at spawn time (HarnessResumeOptions does not currently carry a scenario id).
        if (_scenario.ThrowOnSpawn)
            throw new InvalidOperationException("TestHarness: configured to fail on resume.");

        IHarnessSession instance = new TestHarnessSession(
            instanceId: options.SessionId,
            scenario: _scenario,
            fleetSessionId: options.SessionId,
            scopeFactory: _scopeFactory,
            ownerUserId: options.OwnerUserId);

        return Task.FromResult(instance);
    }

    /// <inheritdoc/>
    public Task<bool> WarmupPooledInstanceAsync(string ownerUserId, CancellationToken ct)
        => Task.FromResult(false);

    private TestScenario ResolveScenario(string? scenarioId)
    {
        // No id → use the in-memory scenario configured via Configure(...). This is the path
        // E2E tests use; they program the scenario from C# before driving the harness.
        if (string.IsNullOrWhiteSpace(scenarioId))
            return _scenario;

        // Live id → load from file. LiveScenarioHarness.Load returns a fallback echo when the
        // file is missing or malformed, so callers never see a null scenario.
        var directory = LiveScenarioHarness.ResolveScenarioDirectory();
        return LiveScenarioHarness.Load(scenarioId, directory);
    }

    // ── Scenario configuration ───────────────────────────────────────────────

    /// <summary>
    /// Configure the scenario for the next <see cref="SpawnAsync"/> call.
    /// Thread-safe via volatile write (scenarios are configured before test action).
    /// </summary>
    public void Configure(TestScenario scenario) => _scenario = scenario;

    public void SetScopeFactory(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    /// <summary>Fluent helper: configure via builder and return the built scenario.</summary>
    public TestScenario Configure(Action<TestScenarioBuilder> configure)
    {
        var builder = new TestScenarioBuilder();
        configure(builder);
        var scenario = builder.Build();
        Configure(scenario);
        return scenario;
    }
}

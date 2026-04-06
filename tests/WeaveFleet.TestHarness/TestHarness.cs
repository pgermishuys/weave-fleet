using WeaveFleet.Application.Harnesses;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.TestHarness;

/// <summary>
/// A mock <see cref="IHarness"/> for E2E and integration tests.
/// Returns <c>Type = "opencode"</c> so the <see cref="SessionOrchestrator"/> selects it
/// by default without requiring production code changes.
/// </summary>
public sealed class TestHarness : IHarness
{
    private TestScenario _scenario = new();

    /// <inheritdoc/>
    /// <remarks>Returns "opencode" so it's selected by the default harness type.</remarks>
    public string Type => "opencode";

    /// <inheritdoc/>
    public string DisplayName => "Test Harness";

    /// <inheritdoc/>
    public HarnessCapabilities Capabilities => new()
    {
        RequiresInitialPrompt = false,
        SupportsAgents = true,
        SupportsModelSelection = true,
        SupportsCommands = true,
        SupportsForking = true,
        SupportsResume = false,    // matches plan spec
        SupportsImageAttachments = true,
        SupportsStreaming = true
    };

    // ── Scenario configuration ───────────────────────────────────────────────

    /// <summary>
    /// Configure the scenario for the next <see cref="SpawnAsync"/> call.
    /// Thread-safe via volatile write (scenarios are configured before test action).
    /// </summary>
    public void Configure(TestScenario scenario) => _scenario = scenario;

    /// <summary>Fluent helper: configure via builder and return the built scenario.</summary>
    public TestScenario Configure(Action<TestScenarioBuilder> configure)
    {
        var builder = new TestScenarioBuilder();
        configure(builder);
        var scenario = builder.Build();
        Configure(scenario);
        return scenario;
    }

    // ── IHarness ────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<HarnessAvailability> CheckAvailabilityAsync(CancellationToken ct)
        => Task.FromResult(new HarnessAvailability(Available: true, Reason: null));

    /// <inheritdoc/>
    public Task<IHarnessInstance> SpawnAsync(HarnessSpawnOptions options, CancellationToken ct)
    {
        if (_scenario.ThrowOnSpawn)
            throw new InvalidOperationException("TestHarness: configured to fail on spawn.");

        IHarnessInstance instance = new TestHarnessInstance(
            instanceId: options.SessionId,
            scenario: _scenario);

        return Task.FromResult(instance);
    }
}

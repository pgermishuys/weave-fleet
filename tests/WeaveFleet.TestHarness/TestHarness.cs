using WeaveFleet.Application.Harnesses;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.TestHarness;

/// <summary>
/// A mock <see cref="IHarness"/> for E2E and integration tests.
/// Returns <c>Type = "opencode"</c> so the <see cref="SessionOrchestrator"/> selects it
/// by default without requiring production code changes.
/// Runtime provisioning is handled by <see cref="TestHarnessRuntime"/>.
/// </summary>
public sealed class TestHarness : IHarness
{
    // ── IHarness ────────────────────────────────────────────────────────────

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
        SupportsResume = true,
        SupportsImageAttachments = true,
        SupportsStreaming = true
    };
}

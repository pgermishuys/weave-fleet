using WeaveFleet.Infrastructure.Harnesses.Copilot;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.Copilot;

public sealed class CopilotHarnessTests
{
    [Fact]
    public void copilot_compacts_and_takes_model_notes()
    {
        var capabilities = new CopilotHarness().Capabilities;

        capabilities.SupportsCompaction.ShouldBeTrue();
        capabilities.TakesModelNotes.ShouldBeTrue();
    }

    [Fact]
    public void the_pinned_runtimes_are_the_ones_compaction_was_probed_on()
        => (CopilotInstaller.WeaveCopilotVersion, CopilotInstaller.CliVersion).ShouldBe(("0.2.0", "1.0.105"));
}

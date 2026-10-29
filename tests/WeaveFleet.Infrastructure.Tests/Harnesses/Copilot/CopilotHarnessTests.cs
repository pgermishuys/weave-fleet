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
    public void copilot_forks_answers_off_the_record_runs_shell_commands_and_takes_side_questions()
    {
        var capabilities = new CopilotHarness().Capabilities;

        (capabilities.SupportsForking, capabilities.SupportsOffTheRecordPrompt, capabilities.SupportsShellCommands, capabilities.SupportsSideConversations)
            .ShouldBe((true, true, true, true));
    }

    [Fact]
    public void the_pinned_runtimes_are_the_ones_compaction_was_probed_on()
        => (CopilotInstaller.WeaveCopilotVersion, CopilotInstaller.CliVersion).ShouldBe(("0.2.1", "1.0.105"));

    [Fact]
    public void copilot_is_a_harness_like_the_others_not_an_experiment()
    {
        var presentation = new CopilotHarness().Presentation;

        presentation.Eyebrow.ShouldBe("CLI harness");
        // What first-run setup says about it, as it does for Claude Code.
        presentation.Pitch.ShouldNotBeNullOrWhiteSpace();
    }
}

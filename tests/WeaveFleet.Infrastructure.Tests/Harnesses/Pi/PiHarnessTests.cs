using WeaveFleet.Infrastructure.Harnesses.Pi;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.Pi;

public sealed class PiHarnessTests
{
    [Fact]
    public void Pi_sessions_cant_be_forked()
    {
        // Pi's fork and clone move the one process a Fleet session runs on over to the copy.
        new PiHarness().Capabilities.SupportsForking.ShouldBeFalse();
    }

    [Fact]
    public void Pi_subagents_have_no_session_to_open_and_finish_inside_their_call()
    {
        // The subagent extension runs them inside the tool call, as processes with no session: Details, not Open.
        var capabilities = new PiHarness().Capabilities;

        capabilities.SupportsChildSessions.ShouldBeFalse();
        capabilities.ChildSessionsResumable.ShouldBeFalse();
        capabilities.ReportsBackgroundWork.ShouldBeFalse();
    }

    [Fact]
    public void Pi_takes_no_notes_for_the_model()
    {
        // A note would have to go into the user's message, which Pi keeps and Fleet shows.
        new PiHarness().Capabilities.TakesModelNotes.ShouldBeFalse();
    }
}

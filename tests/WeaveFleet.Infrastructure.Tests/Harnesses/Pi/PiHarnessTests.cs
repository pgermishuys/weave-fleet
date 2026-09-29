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
}

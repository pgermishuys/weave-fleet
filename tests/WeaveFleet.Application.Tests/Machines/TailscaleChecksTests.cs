using WeaveFleet.Application.Machines;

namespace WeaveFleet.Application.Tests.Machines;

public class TailscaleChecksTests
{
    [Theory]
    [InlineData(null, "missing", false)]
    [InlineData("not json", "missing", false)]
    [InlineData("""{ "BackendState": "Stopped" }""", "stopped", false)]
    [InlineData("""{ "BackendState": "NeedsLogin" }""", "signed-out", false)]
    [InlineData("""{ "BackendState": "Running" }""", "running", true)]
    public void Reads_the_state_Tailscale_is_in(string? json, string state, bool turnOn)
    {
        var check = TailscaleChecks.Read(json, "studio", listenerCoversTailnet: false);

        Assert.Equal(state, check.State);
        Assert.Equal(turnOn, check.CanTurnOn);
        Assert.False(check.Covered);
    }

    [Fact]
    public void A_listener_on_the_tailnet_address_is_covered_and_needs_nothing()
    {
        var check = TailscaleChecks.Read("""{ "BackendState": "Running" }""", "studio", listenerCoversTailnet: true);

        Assert.True(check.Covered);
        Assert.False(check.CanTurnOn);
    }
}

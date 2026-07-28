using WeaveFleet.Application.Configuration;

namespace WeaveFleet.Application.Tests;

public sealed class FleetOptionsTests
{
    [Fact]
    public void default_options_have_expected_values()
    {
        var options = new FleetOptions();
        options.Port.ShouldBe(3000);
        options.Host.ShouldBe("127.0.0.1");
        options.Debug.ShouldBeFalse();
        options.Harness.PooledOpenCodeHarness.ShouldBeFalse();
    }

    [Fact]
    public void listen_url_composes_host_and_port()
    {
        var options = new FleetOptions { Host = "0.0.0.0", Port = 8080 };
        options.ListenUrl.ShouldBe("http://0.0.0.0:8080");
    }

    [Fact]
    public void pooled_opencode_harness_can_be_enabled()
    {
        var options = new FleetOptions
        {
            Harness = new HarnessOptions { PooledOpenCodeHarness = true }
        };

        options.Harness.PooledOpenCodeHarness.ShouldBeTrue();
    }
}

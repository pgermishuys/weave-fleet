using WeaveFleet.Application.Configuration;

namespace WeaveFleet.Application.Tests;

public sealed class FleetOptionsTests
{
    [Fact]
    public void DefaultOptions_HaveExpectedValues()
    {
        var options = new FleetOptions();
        options.Port.ShouldBe(3000);
        options.Host.ShouldBe("127.0.0.1");
        options.Debug.ShouldBeFalse();
    }

    [Fact]
    public void ListenUrl_ComposesHostAndPort()
    {
        var options = new FleetOptions { Host = "0.0.0.0", Port = 8080 };
        options.ListenUrl.ShouldBe("http://0.0.0.0:8080");
    }
}

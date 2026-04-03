using WeaveFleet.Application.Configuration;

namespace WeaveFleet.Application.Tests;

public sealed class FleetOptionsTests
{
    [Fact]
    public void DefaultOptions_HaveExpectedValues()
    {
        var options = new FleetOptions();
        Assert.Equal(3000, options.Port);
        Assert.Equal("127.0.0.1", options.Host);
        Assert.False(options.Debug);
    }

    [Fact]
    public void ListenUrl_ComposesHostAndPort()
    {
        var options = new FleetOptions { Host = "0.0.0.0", Port = 8080 };
        Assert.Equal("http://0.0.0.0:8080", options.ListenUrl);
    }
}

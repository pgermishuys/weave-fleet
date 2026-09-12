using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using WeaveFleet.Application.Configuration;

namespace WeaveFleet.Api.Tests;

public sealed class LocalFleetUrlTests
{
    [Theory]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("0.0.0.0", "127.0.0.1")]
    [InlineData("localhost", "127.0.0.1")]
    [InlineData("*", "127.0.0.1")]
    [InlineData("+", "127.0.0.1")]
    [InlineData("::", "127.0.0.1")]
    [InlineData("[::]", "127.0.0.1")]
    [InlineData("::1", "[::1]")]
    [InlineData("192.168.1.5", "192.168.1.5")]
    public void The_host_becomes_loopback_when_Fleet_listens_everywhere(string host, string expected)
    {
        LocalFleetUrl.LoopbackHost(host).ShouldBe(expected);
    }

    [Fact]
    public void A_configured_port_is_used_before_the_server_has_started()
    {
        var url = new LocalFleetUrl(new FleetOptions { Host = "0.0.0.0", Port = 2113 }, new FakeServer());

        url.TryGet().ShouldBe("http://127.0.0.1:2113");
    }

    [Fact]
    public void Port_zero_uses_the_port_the_server_bound()
    {
        var options = new FleetOptions { Host = "127.0.0.1", Port = 0 };

        new LocalFleetUrl(options, new FakeServer()).TryGet().ShouldBeNull();
        new LocalFleetUrl(options, new FakeServer("http://127.0.0.1:45123")).TryGet().ShouldBe("http://127.0.0.1:45123");
    }

    private sealed class FakeServer : IServer
    {
        public FakeServer(params string[] addresses)
        {
            var feature = new ServerAddressesFeature();
            foreach (var address in addresses)
                feature.Addresses.Add(address);
            Features.Set<IServerAddressesFeature>(feature);
        }

        public IFeatureCollection Features { get; } = new FeatureCollection();

        public void Dispose()
        {
        }

        public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken)
            where TContext : notnull
            => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

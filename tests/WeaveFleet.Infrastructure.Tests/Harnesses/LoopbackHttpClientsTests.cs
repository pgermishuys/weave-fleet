using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Infrastructure.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;

namespace WeaveFleet.Infrastructure.Tests.Harnesses;

public class LoopbackHttpClientsTests
{
    [Fact]
    public void A_loopback_client_never_goes_through_the_environment_proxy()
    {
        using var provider = new ServiceCollection()
            .AddLoopbackHttpClient(OpenCode2HarnessRuntime.HttpClientName)
            .Services.BuildServiceProvider();

        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(OpenCode2HarnessRuntime.HttpClientName);
        while (handler is DelegatingHandler delegating)
            handler = delegating.InnerHandler!;

        var primary = Assert.IsType<SocketsHttpHandler>(handler);
        Assert.False(primary.UseProxy);
    }
}

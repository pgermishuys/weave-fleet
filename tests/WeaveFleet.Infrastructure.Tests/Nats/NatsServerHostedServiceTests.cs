using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Infrastructure.Nats;

namespace WeaveFleet.Infrastructure.Tests.Nats;

public sealed class NatsServerHostedServiceTests
{
    [Fact]
    public async Task ExternalUrlSet_doesNotLaunchSubprocess()
    {
        var options = new NatsOptions { ExternalUrl = "nats://localhost:4222" };
        var sut = new NatsServerHostedService(options, NullLogger<NatsServerHostedService>.Instance);

        await sut.StartAsync(CancellationToken.None);
        try
        {
            sut.IsEmbeddedRunning.ShouldBeFalse();
            sut.ResolvedUrl.ShouldBe("nats://localhost:4222");
        }
        finally
        {
            await sut.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task NoExternalUrl_launchesSubprocessOnLoopback()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nats-hosted-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = new NatsOptions { DataDirectory = dir };
            var sut = new NatsServerHostedService(options, NullLogger<NatsServerHostedService>.Instance);

            await sut.StartAsync(CancellationToken.None);
            try
            {
                sut.IsEmbeddedRunning.ShouldBeTrue();
                sut.ResolvedUrl.ShouldStartWith("nats://127.0.0.1:");
            }
            finally
            {
                await sut.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }
    }
}

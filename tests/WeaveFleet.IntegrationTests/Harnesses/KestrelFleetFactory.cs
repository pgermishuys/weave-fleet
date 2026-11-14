using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace WeaveFleet.IntegrationTests.Harnesses;

/// <summary>Fleet on a real Kestrel port, so a harness process Fleet starts can call the bridge.</summary>
/// <param name="openCodeRequestTimeout">
/// How long Fleet's ordinary OpenCode requests wait, when a test needs it shorter than the default 100 s (a slow
/// first request then fails in seconds, instead of only on a slow machine).
/// </param>
internal sealed class KestrelFleetFactory(string dbPath, TimeSpan? openCodeRequestTimeout = null) : WebApplicationFactory<Program>
{
    private IHost? _host;

    public bool IsStarted => _host is not null;

    public IServiceProvider LiveServices => _host?.Services ?? throw new InvalidOperationException("Not started");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // No Bun for the mod host, whatever this machine has installed: tests never start a real one here.
        builder.ConfigureTestServices(services => services.AddSingleton<WeaveFleet.Application.Mods.Host.IModHostBun, WeaveFleet.Testing.Fakes.NoModHostBun>());
        builder.UseEnvironment("Testing");
        builder.UseSetting("Fleet:DatabasePath", dbPath);
        builder.UseSetting("Fleet:AnalyticsDatabasePath", Path.ChangeExtension(dbPath, ".analytics.db"));
        builder.UseSetting("Fleet:AnalyticsEnabled", "false");
        builder.UseSetting("Fleet:Host", "127.0.0.1");
        builder.UseSetting("Fleet:Port", "0");
        builder.UseSetting("Fleet:Auth:Enabled", "false");
        builder.UseSetting("Fleet:Auth:TokenAuthEnabled", "false");
        builder.ConfigureServices(services =>
        {
            if (openCodeRequestTimeout is { } timeout)
                services.AddHttpClient("OpenCode").ConfigureHttpClient(client => client.Timeout = timeout);

            // Warmup would start an extra opencode process before Fleet knows its port. Matched by name, as the other
            // test hosts match OpenCode's PortAllocator.
            foreach (var descriptor in services.Where(d => d.ImplementationType?.Name == "OpenCodeWarmupHostedService").ToList())
                services.Remove(descriptor);
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureWebHost(web => web.UseKestrel());
        _host = builder.Build();
        _host.Start();
        _host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.ShouldNotBeEmpty();

        // A throwaway host for the base class, so it doesn't start a second app on the same database.
        return Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .ConfigureWebHost(web => web.UseTestServer())
            .Build();
    }

    public override async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        await base.DisposeAsync();
    }
}

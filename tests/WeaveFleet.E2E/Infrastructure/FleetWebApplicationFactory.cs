using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Infrastructure;
using WeaveFleet.Infrastructure.Harnesses.ClaudeCode;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;

namespace WeaveFleet.E2E.Infrastructure;

/// <summary>
/// Boots the real ASP.NET Core pipeline with a <see cref="TestHarness.TestHarness"/> replacing
/// all production harness registrations. Uses an isolated per-test SQLite database.
/// Starts a real Kestrel server (not the in-memory TestServer) so Playwright can connect.
/// </summary>
public sealed class FleetWebApplicationFactory : WebApplicationFactory<Program>, IAsyncDisposable
{
    private readonly string _dbPath;
    private readonly string _analyticsDbPath;
    private string? _kestrelUrl;
    private IHost? _host;

    public FleetWebApplicationFactory()
    {
        var guid = Guid.NewGuid().ToString("N");
        _dbPath = Path.Combine(Path.GetTempPath(), $"weave-fleet-test-{guid}.db");
        _analyticsDbPath = Path.Combine(Path.GetTempPath(), $"weave-fleet-analytics-test-{guid}.db");
    }

    /// <summary>The <see cref="TestHarness.TestHarness"/> singleton registered in this factory's DI container.</summary>
    public TestHarness.TestHarness TestHarness { get; } = new();

    /// <summary>The <see cref="TestHarness.TestHarnessRuntime"/> singleton registered in this factory's DI container.</summary>
    public TestHarness.TestHarnessRuntime TestHarnessRuntime { get; } = new();

    /// <summary>
    /// The base URL the Kestrel server listens on (e.g. "http://127.0.0.1:54321").
    /// Only available after the host has been started (triggered by calling <see cref="EnsureStartedAsync"/>).
    /// </summary>
    public string ServerUrl => _kestrelUrl
        ?? throw new InvalidOperationException("Server not started. Call EnsureStartedAsync() first.");

    /// <summary>
    /// Returns the DI service provider from the running Kestrel host.
    /// Use this instead of <see cref="WebApplicationFactory{TEntryPoint}.Services"/> which
    /// fails because it casts the server to <c>TestServer</c> internally.
    /// </summary>
    public IServiceProvider KestrelServices => _host?.Services
        ?? throw new InvalidOperationException("Server not started. Call EnsureStartedAsync() first.");

    /// <summary>
    /// Starts the Kestrel server if not already running.
    /// Must be called before accessing <see cref="ServerUrl"/> or <see cref="KestrelServices"/>.
    /// </summary>
    public async Task EnsureStartedAsync()
    {
        if (_host is not null)
            return;

        // CreateDefaultClient() triggers the full host creation pipeline (ConfigureWebHost → CreateHost)
        // but we don't actually need the HttpClient — we just need the side effect of host creation.
        // However, CreateDefaultClient fails because there's no TestServer. Instead, use the
        // internal mechanism: calling CreateHost indirectly through the Server property causes the cast issue.
        // The safest path: manually invoke the host builder pipeline.
        await Task.CompletedTask; // keep async signature for future use

        // Force host creation by building the host through the factory's pipeline.
        // GetTestServer() would fail, so we trigger via a method that calls EnsureServer() → CreateHost().
        // The trick: override CreateHost to store the host, then try/catch the Services accessor.
        try
        {
            _ = Services;
        }
        catch (InvalidCastException)
        {
            // Expected — base.Services tries to cast KestrelServerImpl to TestServer.
            // By this point, CreateHost() has already been called and _host is set.
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            RemoveProductionHarnessRegistrations(services);

            // ── Remove all production IHarness registrations ─────────────────
            var harnessDescriptors = services
                .Where(d => d.ServiceType == typeof(IHarness))
                .ToList();
            foreach (var d in harnessDescriptors)
                services.Remove(d);

            // ── Remove all production IHarnessRuntime registrations ──────────
            var harnessRuntimeDescriptors = services
                .Where(d => d.ServiceType == typeof(IHarnessRuntime))
                .ToList();
            foreach (var d in harnessRuntimeDescriptors)
                services.Remove(d);

            // ── Register the TestHarness singleton for both interfaces ────────
            services.AddSingleton<IHarness>(TestHarness);
            services.AddSingleton<IHarnessRuntime>(TestHarnessRuntime);

            // ── Remove PortAllocator (not needed without OpenCode) ────────────
            var portAllocatorDescriptors = services
                .Where(d => d.ServiceType.Name == "PortAllocator")
                .ToList();
            foreach (var d in portAllocatorDescriptors)
                services.Remove(d);
        });

        // Override configuration to use isolated DB paths for E2E.
        builder.UseSetting("Fleet:DatabasePath", _dbPath);
        builder.UseSetting("Fleet:AnalyticsEnabled", "true");
        builder.UseSetting("Fleet:Auth:Enabled", "false");
        builder.UseSetting("Fleet:Port", "0");

        // Use environment-based configuration to override FleetOptions singleton
        builder.ConfigureServices(services =>
        {
            // Override the FleetOptions singleton that was already registered by AddFleetInfrastructure
            var existingOptions = services.FirstOrDefault(d =>
                d.ServiceType == typeof(FleetOptions) &&
                d.Lifetime == ServiceLifetime.Singleton);
            if (existingOptions is not null)
                services.Remove(existingOptions);

            // Also need to remove the SqliteConnectionFactory that was seeded with old options
            var connFactoryDescriptors = services
                .Where(d => d.ImplementationType?.Name == "SqliteConnectionFactory")
                .ToList();
            foreach (var d in connFactoryDescriptors)
                services.Remove(d);

            var testOptions = new FleetOptions
            {
                DatabasePath = _dbPath,
                AnalyticsEnabled = true,
                Port = 0,
                Host = "127.0.0.1"
            };

            services.AddSingleton(testOptions);
            services.AddSingleton(new PortAllocator(
                testOptions.HarnessPortRangeStart,
                testOptions.HarnessPortRangeEnd));

            // Re-register the SqliteConnectionFactory with test options
            services.AddSingleton<WeaveFleet.Application.Data.IDbConnectionFactory>(
                _ => new WeaveFleet.Infrastructure.Data.SqliteConnectionFactory(testOptions));
        });

        // ── Use ephemeral port for Kestrel (overrides "Urls" in appsettings.json) ──
        builder.UseUrls("http://127.0.0.1:0");
    }

    private static void RemoveProductionHarnessRegistrations(IServiceCollection services)
    {
        var concreteDescriptors = services
            .Where(d => d.ServiceType == typeof(OpenCodeHarness)
                || d.ServiceType == typeof(OpenCodeHarnessRuntime)
                || d.ServiceType == typeof(ClaudeCodeHarness)
                || d.ServiceType == typeof(ClaudeCodeHarnessRuntime))
            .ToList();

        foreach (var descriptor in concreteDescriptors)
            services.Remove(descriptor);
    }

    /// <summary>
     /// Overrides host creation to use a real Kestrel TCP server instead of the in-memory TestServer.
     /// Playwright (a real browser) cannot connect to TestServer — it needs a real TCP endpoint.
     /// </summary>
    protected override IHost CreateHost(IHostBuilder builder)
    {
        // DO NOT call base.CreateHost — that adds UseTestServer() which replaces Kestrel
        // with an in-memory handler that only works via CreateClient().
        // Instead, explicitly wire up Kestrel and start the host with a real TCP listener.
        builder.ConfigureWebHost(webBuilder => webBuilder.UseKestrel());

        _host = builder.Build();
        _host.Start();

        // Extract the dynamically assigned port from Kestrel
        var server = _host.Services.GetRequiredService<IServer>();
        var addressFeature = server.Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException(
                "Could not determine server URL. IServerAddressesFeature not available.");
        _kestrelUrl = addressFeature.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException(
                "Kestrel did not report any listening addresses.");

        return _host;
    }

    /// <summary>Clean up temporary database files on disposal.</summary>
    public new async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();

        TryDeleteFile(_dbPath);
        TryDeleteFile($"{_dbPath}-wal");
        TryDeleteFile($"{_dbPath}-shm");
        TryDeleteFile(_analyticsDbPath);
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }
}

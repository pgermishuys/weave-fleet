extern alias IdpHost;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Infrastructure;
using WeaveFleet.Infrastructure.Data;
using WeaveFleet.Infrastructure.Harnesses.ClaudeCode;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;

namespace WeaveFleet.E2E.Infrastructure;

/// <summary>
/// A variant of <see cref="FleetWebApplicationFactory"/> that boots Fleet with:
/// <list type="bullet">
///   <item><c>Auth.Enabled = true</c> pointing at the test Duende IdP.</item>
///   <item>HTTPS on <c>https://127.0.0.1:{ephemeral-port}</c> using the .NET dev certificate.</item>
///   <item>Cloud mode enabled with a temp workspace root.</item>
/// </list>
///
/// <b>Startup sequence:</b>
/// <list type="number">
///   <item>Construct the factory — creates the <see cref="IdpProcessHost"/>.</item>
///   <item>Call <see cref="EnsureStartedAsync"/> — starts the IdP, then Fleet's Kestrel host.</item>
///   <item>Access <see cref="ServerUrl"/> for the Fleet HTTPS base URL.</item>
/// </list>
/// </summary>
public sealed class AuthFleetWebApplicationFactory : WebApplicationFactory<Program>, IAsyncDisposable
{
    private readonly string _dbPath;
    private readonly string _analyticsDbPath;
    private readonly string _workspaceRoot;
    private readonly IdpProcessHost _idp;
    private string? _kestrelUrl;
    private IHost? _host;

    public AuthFleetWebApplicationFactory()
    {
        var guid = Guid.NewGuid().ToString("N");
        _dbPath = Path.Combine(Path.GetTempPath(), $"weave-fleet-auth-test-{guid}.db");
        _analyticsDbPath = Path.Combine(Path.GetTempPath(), $"weave-fleet-auth-analytics-test-{guid}.db");
        _workspaceRoot = Path.Combine(Path.GetTempPath(), $"weave-fleet-auth-workspace-{guid}");
        Directory.CreateDirectory(_workspaceRoot);

        _idp = new IdpProcessHost();
    }

    /// <summary>The <see cref="WeaveFleet.TestHarness.TestHarness"/> singleton registered in this factory.</summary>
    public TestHarness.TestHarness TestHarness { get; } = new();

    /// <summary>The <see cref="WeaveFleet.TestHarness.TestHarnessRuntime"/> singleton registered in this factory.</summary>
    public TestHarness.TestHarnessRuntime TestHarnessRuntime { get; } = new();

    /// <summary>
    /// The HTTPS base URL Fleet listens on (e.g. <c>https://127.0.0.1:54321</c>).
    /// Only available after <see cref="EnsureStartedAsync"/> completes.
    /// </summary>
    public string ServerUrl => _kestrelUrl
        ?? throw new InvalidOperationException(
            "Server not started. Call EnsureStartedAsync() first.");

    /// <summary>
    /// The authority URL of the Duende test IdP (e.g. <c>https://127.0.0.1:54399</c>).
    /// </summary>
    public string IdpAuthority => _idp.Authority;

    /// <summary>
    /// Returns the DI service provider from the running Kestrel host.
    /// Use this instead of <see cref="WebApplicationFactory{TEntryPoint}.Services"/>.
    /// </summary>
    public IServiceProvider KestrelServices => _host?.Services
        ?? throw new InvalidOperationException(
            "Server not started. Call EnsureStartedAsync() first.");

    /// <summary>
    /// Starts the IdP (if not already running), then starts Fleet's Kestrel host.
    /// Idempotent — safe to call multiple times.
    /// </summary>
    public async Task EnsureStartedAsync()
    {
        if (_host is not null)
            return;

        // Phase 1: start IdP first so we know the Authority URL
        await _idp.StartAsync();

        // Phase 2: inject auth configuration via environment variables.
        // Program.cs reads FleetOptions from IConfiguration at boot time to decide
        // whether to register OIDC middleware and the "FleetUser" authorization policy.
        // ConfigureWebHost runs too late (after Program.cs inline code), so we must set
        // these values in the environment before the host builder runs.
        SetAuthEnvironmentVariables();

        // Phase 3: build and start Fleet
        // Force host creation by triggering the factory pipeline.
        // Services accessor throws InvalidCastException because we use real Kestrel, not TestServer.
        try
        {
            _ = Services;
        }
        catch (InvalidCastException)
        {
            // Expected — base.Services casts to TestServer which fails.
            // CreateHost() has already been called and _host is set by now.
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            RemoveProductionHarnessRegistrations(services);

            // ── Remove all production IHarness registrations ────────────────
            var harnessDescriptors = services
                .Where(d => d.ServiceType == typeof(IHarness))
                .ToList();
            foreach (var d in harnessDescriptors)
                services.Remove(d);

            // ── Remove all production IHarnessRuntime registrations ─────────
            var harnessRuntimeDescriptors = services
                .Where(d => d.ServiceType == typeof(IHarnessRuntime))
                .ToList();
            foreach (var d in harnessRuntimeDescriptors)
                services.Remove(d);

            // ── Register TestHarness for both interfaces ────────────────────
            services.AddSingleton<IHarness>(TestHarness);
            services.AddSingleton<IHarnessRuntime>(sp =>
            {
                TestHarnessRuntime.SetScopeFactory(sp.GetRequiredService<IServiceScopeFactory>());
                return TestHarnessRuntime;
            });

            // ── Remove PortAllocator ────────────────────────────────────────
            var portAllocatorDescriptors = services
                .Where(d => d.ServiceType.Name == "PortAllocator")
                .ToList();
            foreach (var d in portAllocatorDescriptors)
                services.Remove(d);

            // ── Override FleetOptions singleton with test values ────────────
            var existingOptions = services.FirstOrDefault(d =>
                d.ServiceType == typeof(FleetOptions) &&
                d.Lifetime == ServiceLifetime.Singleton);
            if (existingOptions is not null)
                services.Remove(existingOptions);

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
                Host = "127.0.0.1",
                Auth = new AuthOptions
                {
                    Enabled = true,
                    Authority = _idp.Authority,
                    ClientId = IdpHost::WeaveFleet.IdP.Config.FleetClientId,
                    ClientSecret = IdpHost::WeaveFleet.IdP.Config.FleetClientSecret,
                    CallbackPath = "/auth/callback",
                    SignedOutCallbackPath = "/auth/signed-out",
                    // AllowedOrigins will be updated after Fleet's port is known (see CreateHost)
                    AllowedOrigins = []
                },
                Cloud = new CloudOptions
                {
                    Enabled = true,
                    WorkspaceRoot = _workspaceRoot
                }
            };

            services.AddSingleton(testOptions);
            services.AddSingleton(new PortAllocator(
                testOptions.HarnessPortRangeStart,
                testOptions.HarnessPortRangeEnd));
            services.AddSingleton<IDbConnectionFactory>(
                _ => new SqliteConnectionFactory(testOptions));
        });

        // Auth-enabled mode: bind Fleet to 127.0.0.1 over HTTPS using the .NET dev certificate
        builder.UseUrls();  // Clear default URLs — Kestrel configuration below sets actual bindings
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

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureWebHost(webBuilder =>
        {
            webBuilder.UseKestrel(kestrel =>
            {
                kestrel.Listen(
                    System.Net.IPAddress.Loopback,
                    port: 0,
                    opts => opts.UseHttps());
            });
        });

        _host = builder.Build();
        _host.Start();

        // Extract the bound port and build the Fleet HTTPS URL
        var server = _host.Services.GetRequiredService<IServer>();
        var addressFeature = server.Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException(
                "IServerAddressesFeature not available after Fleet start.");

        var address = addressFeature.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException(
                "Fleet Kestrel did not report any listening addresses.");

        var uri = new Uri(address);
        _kestrelUrl = $"https://127.0.0.1:{uri.Port}";

        // Back-fill AllowedOrigins now that we know the Fleet URL
        var fleetOptions = _host.Services.GetRequiredService<FleetOptions>();
        fleetOptions.Auth.AllowedOrigins = [_kestrelUrl];

        return _host;
    }

    /// <summary>Clean up temporary resources on disposal.</summary>
    public new async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _idp.DisposeAsync();

        ClearAuthEnvironmentVariables();

        TryDeleteFile(_dbPath);
        TryDeleteFile($"{_dbPath}-wal");
        TryDeleteFile($"{_dbPath}-shm");
        TryDeleteFile(_analyticsDbPath);
        TryDeleteDirectory(_workspaceRoot);
    }

    /// <summary>
    /// Sets environment variables so that Program.cs reads <c>Auth.Enabled = true</c>
    /// and the correct OIDC client settings when it captures <see cref="FleetOptions"/>
    /// from <see cref="Microsoft.Extensions.Configuration.IConfiguration"/> at boot time.
    /// </summary>
    private void SetAuthEnvironmentVariables()
    {
        // ASP.NET Core maps "__" to ":" for nested configuration keys in environment variables.
        Environment.SetEnvironmentVariable("Fleet__Auth__Enabled", "true");
        Environment.SetEnvironmentVariable("Fleet__Auth__Authority", _idp.Authority);
        Environment.SetEnvironmentVariable(
            "Fleet__Auth__ClientId",
            IdpHost::WeaveFleet.IdP.Config.FleetClientId);
        Environment.SetEnvironmentVariable(
            "Fleet__Auth__ClientSecret",
            IdpHost::WeaveFleet.IdP.Config.FleetClientSecret);
        Environment.SetEnvironmentVariable("Fleet__Auth__CallbackPath", "/auth/callback");
        Environment.SetEnvironmentVariable("Fleet__Auth__SignedOutCallbackPath", "/auth/signed-out");
        Environment.SetEnvironmentVariable("Fleet__Cloud__Enabled", "true");
        Environment.SetEnvironmentVariable("Fleet__Cloud__WorkspaceRoot", _workspaceRoot);
        Environment.SetEnvironmentVariable("Fleet__DatabasePath", _dbPath);
        Environment.SetEnvironmentVariable("Fleet__AnalyticsEnabled", "true");
        Environment.SetEnvironmentVariable("Fleet__Port", "0");
        Environment.SetEnvironmentVariable("Fleet__Host", "127.0.0.1");
    }

    /// <summary>
    /// Removes the environment variables set by <see cref="SetAuthEnvironmentVariables"/>
    /// to avoid leaking state to other tests in the process.
    /// </summary>
    private static void ClearAuthEnvironmentVariables()
    {
        Environment.SetEnvironmentVariable("Fleet__Auth__Enabled", null);
        Environment.SetEnvironmentVariable("Fleet__Auth__Authority", null);
        Environment.SetEnvironmentVariable("Fleet__Auth__ClientId", null);
        Environment.SetEnvironmentVariable("Fleet__Auth__ClientSecret", null);
        Environment.SetEnvironmentVariable("Fleet__Auth__CallbackPath", null);
        Environment.SetEnvironmentVariable("Fleet__Auth__SignedOutCallbackPath", null);
        Environment.SetEnvironmentVariable("Fleet__Cloud__Enabled", null);
        Environment.SetEnvironmentVariable("Fleet__Cloud__WorkspaceRoot", null);
        Environment.SetEnvironmentVariable("Fleet__DatabasePath", null);
        Environment.SetEnvironmentVariable("Fleet__AnalyticsEnabled", null);
        Environment.SetEnvironmentVariable("Fleet__Port", null);
        Environment.SetEnvironmentVariable("Fleet__Host", null);
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { /* best effort */ }
    }
}

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure;
using WeaveFleet.Infrastructure.Data;

namespace WeaveFleet.E2E.Infrastructure;

/// <summary>
/// Boots Fleet with the real production harness registrations for opt-in harness smoke tests.
/// Uses isolated SQLite databases, an ephemeral Kestrel port, and a per-run temporary working
/// directory that smoke tests should pass when creating real harness-backed sessions.
/// </summary>
public sealed class SmokeFleetWebApplicationFactory : WebApplicationFactory<Program>, IAsyncDisposable
{
    private const string SmokeEnvironmentVariable = "FLEET_HARNESS_SMOKE";

    private readonly HarnessSmokeSpec _spec;
    private readonly string _dbPath;
    private readonly string _analyticsDbPath;
    private readonly string _workingDirectory;
    private string? _kestrelUrl;
    private IHost? _host;
    private bool _smokeDataConfigured;

    public SmokeFleetWebApplicationFactory(HarnessSmokeSpec spec)
    {
        _spec = spec;
        var guid = Guid.NewGuid().ToString("N");
        _dbPath = Path.Combine(Path.GetTempPath(), $"fleet-smoke-{guid}.db");
        _analyticsDbPath = Path.Combine(Path.GetTempPath(), $"fleet-smoke-analytics-{guid}.db");
        _workingDirectory = Path.Combine(Path.GetTempPath(), $"fleet-{SanitizePathSegment(spec.HarnessType)}-smoke-{guid}");
        Directory.CreateDirectory(_workingDirectory);
    }

    /// <summary>The base URL the Kestrel server listens on after <see cref="EnsureStartedAsync"/>.</summary>
    public string ServerUrl => _kestrelUrl
        ?? throw new InvalidOperationException("Server not started. Call EnsureStartedAsync() first.");

    /// <summary>The per-run temporary directory smoke tests should use as the real harness session directory.</summary>
    public string WorkingDirectory => _workingDirectory;

    /// <summary>Returns the DI service provider from the running Kestrel host.</summary>
    public IServiceProvider KestrelServices => _host?.Services
        ?? throw new InvalidOperationException("Server not started. Call EnsureStartedAsync() first.");

    /// <summary>Starts the Kestrel server if not already running.</summary>
    public async Task EnsureStartedAsync()
    {
        if (_host is not null)
        {
            await ConfigureSmokeDataAsync();
            return;
        }

        EnsureSmokeOptIn();
        EnsureNoTestHarnessOverride();

        try
        {
            _ = Services;
        }
        catch (InvalidCastException)
        {
            // Expected — base.Services tries to cast KestrelServerImpl to TestServer.
            // CreateHost() has already run and stored the real Kestrel host.
        }

        await ConfigureSmokeDataAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Fleet:DatabasePath", _dbPath);
        builder.UseSetting("Fleet:AnalyticsDatabasePath", _analyticsDbPath);
        builder.UseSetting("Fleet:AnalyticsEnabled", "true");
        builder.UseSetting("Fleet:Auth:Enabled", "false");
        builder.UseSetting("Fleet:Auth:TokenAuthEnabled", "false");
        builder.UseSetting("Fleet:Port", "0");

        builder.ConfigureServices(services =>
        {
            var existingOptions = services.FirstOrDefault(d =>
                d.ServiceType == typeof(FleetOptions) &&
                d.Lifetime == ServiceLifetime.Singleton);
            if (existingOptions is not null)
                services.Remove(existingOptions);

            var connFactoryDescriptors = services
                .Where(d => d.ImplementationType?.Name == "SqliteConnectionFactory")
                .ToList();
            foreach (var descriptor in connFactoryDescriptors)
                services.Remove(descriptor);

            var smokeOptions = new FleetOptions
            {
                DatabasePath = _dbPath,
                AnalyticsDatabasePath = _analyticsDbPath,
                AnalyticsEnabled = true,
                Port = 0,
                Host = "127.0.0.1",
                Auth = new AuthOptions
                {
                    Enabled = false,
                    TokenAuthEnabled = false
                },
            };

            services.AddSingleton(smokeOptions);
            services.AddSingleton<IDbConnectionFactory>(_ => new SqliteConnectionFactory(smokeOptions));
        });

        builder.UseUrls("http://127.0.0.1:0");
        builder.UseSetting("Urls", "http://127.0.0.1:0");
        builder.ConfigureAppConfiguration(config =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Urls"] = "http://127.0.0.1:0",
                ["Fleet:DatabasePath"] = _dbPath,
                ["Fleet:AnalyticsDatabasePath"] = _analyticsDbPath,
                ["Fleet:AnalyticsEnabled"] = "true",
                ["Fleet:Auth:Enabled"] = "false",
                ["Fleet:Auth:TokenAuthEnabled"] = "false",
                ["Fleet:Port"] = "0",
                ["Fleet:Host"] = "127.0.0.1"
            });
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureWebHost(webBuilder => webBuilder.UseKestrel());

        _host = builder.Build();
        _host.Start();

        var server = _host.Services.GetRequiredService<IServer>();
        var addressFeature = server.Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException(
                "Could not determine server URL. IServerAddressesFeature not available.");

        _kestrelUrl = addressFeature.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException(
                "Kestrel did not report any listening addresses.");

        return _host;
    }

    public new async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();

        TryDeleteFile(_dbPath);
        TryDeleteFile($"{_dbPath}-wal");
        TryDeleteFile($"{_dbPath}-shm");
        TryDeleteFile(_analyticsDbPath);
        TryDeleteFile($"{_analyticsDbPath}-wal");
        TryDeleteFile($"{_analyticsDbPath}-shm");
        TryDeleteDirectory(_workingDirectory);
    }

    private static void EnsureSmokeOptIn()
    {
        var value = Environment.GetEnvironmentVariable(SmokeEnvironmentVariable);
        if (!string.Equals(value, "1", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Harness smoke tests are opt-in. Set {SmokeEnvironmentVariable}=1 before starting {nameof(SmokeFleetWebApplicationFactory)}.");
        }
    }

    private static void EnsureNoTestHarnessOverride()
    {
        var harnessMode = Environment.GetEnvironmentVariable("FLEET_HARNESS");
        if (string.Equals(harnessMode, "test", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "FLEET_HARNESS=test replaces production harnesses and is incompatible with harness smoke tests.");
        }
    }

    private async Task ConfigureSmokeDataAsync()
    {
        if (_smokeDataConfigured)
            return;

        var services = _host?.Services
            ?? throw new InvalidOperationException("Server not started. Call EnsureStartedAsync() first.");

        await using var scope = services.CreateAsyncScope();
        var registry = scope.ServiceProvider.GetRequiredService<IHarnessRegistry>();
        if (registry.GetByType(_spec.HarnessType) is null)
        {
            throw new InvalidOperationException(
                $"Smoke factory expected a real harness registration for '{_spec.HarnessType}' to remain registered in DI.");
        }

        if (registry.GetRuntimeByType(_spec.HarnessType) is null)
        {
            throw new InvalidOperationException(
                $"Smoke factory expected a real harness runtime registration for '{_spec.HarnessType}' to remain registered in DI.");
        }

        var preferences = scope.ServiceProvider.GetRequiredService<IUserPreferenceRepository>();
        await preferences.SetAsync(_spec.EnabledPreferenceKey, "true");
        foreach (var disabledHarnessPreferenceKey in _spec.DisabledHarnessPreferenceKeys)
            await preferences.SetAsync(disabledHarnessPreferenceKey, "false");

        await preferences.SetAsync("defaultHarnessType", _spec.HarnessType);

        var workspaceRootService = scope.ServiceProvider.GetRequiredService<WorkspaceRootService>();
        var addRootResult = await workspaceRootService.AddRootAsync(_workingDirectory);
        if (addRootResult.IsFailure &&
            !addRootResult.Error.Description.Contains("already registered", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Failed to register smoke workspace root '{_workingDirectory}': {addRootResult.Error.Description}");
        }

        _smokeDataConfigured = true;
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

    private static string SanitizePathSegment(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(character => invalidChars.Contains(character) ? '-' : character));
    }
}

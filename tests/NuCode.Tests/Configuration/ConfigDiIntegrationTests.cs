using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NuCode.Configuration;

namespace NuCode;

public sealed class ConfigDiIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public ConfigDiIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nucode-di-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveNuCodeConfigFromDi()
    {
        var services = new ServiceCollection();
        services.AddNuCode(options =>
        {
            options.WorkingDirectory = _tempDir;
        });

        using var provider = services.BuildServiceProvider();
        var config = provider.GetRequiredService<IOptions<NuCodeConfig>>().Value;

        config.ShouldNotBeNull();
    }

    [Fact]
    public void DiConfigLoadsProjectFile()
    {
        File.WriteAllText(
            Path.Combine(_tempDir, "nucode.jsonc"),
            """
            {
                // Project config
                "model": "claude-sonnet-4",
                "logLevel": "debug"
            }
            """);

        var services = new ServiceCollection();
        services.AddNuCode(options =>
        {
            options.WorkingDirectory = _tempDir;
        });

        using var provider = services.BuildServiceProvider();
        var config = provider.GetRequiredService<IOptions<NuCodeConfig>>().Value;

        config.Model.ShouldBe("claude-sonnet-4");
        config.LogLevel.ShouldBe("debug");
    }

    [Fact]
    public void DiProgrammaticConfigOverridesFile()
    {
        File.WriteAllText(
            Path.Combine(_tempDir, "nucode.jsonc"),
            """{"model": "from-file", "logLevel": "info"}""");

        var services = new ServiceCollection();
        services.AddNuCode(options =>
        {
            options.WorkingDirectory = _tempDir;
            options.Config = new NuCodeConfig { Model = "from-code" };
        });

        using var provider = services.BuildServiceProvider();
        var config = provider.GetRequiredService<IOptions<NuCodeConfig>>().Value;

        config.Model.ShouldBe("from-code");
        config.LogLevel.ShouldBe("info"); // from file, not overridden
    }

    [Fact]
    public void DiRegistersIOptionsMonitor()
    {
        var services = new ServiceCollection();
        services.AddNuCode(options =>
        {
            options.WorkingDirectory = _tempDir;
        });

        using var provider = services.BuildServiceProvider();
        var monitor = provider.GetRequiredService<IOptionsMonitor<NuCodeConfig>>();

        monitor.ShouldNotBeNull();
        monitor.CurrentValue.ShouldNotBeNull();
    }

    [Fact]
    public void OptionsMonitorFiresChangeOnFileModification()
    {
        // Create initial config file
        var configPath = Path.Combine(_tempDir, "nucode.jsonc");
        File.WriteAllText(configPath, """{"model": "initial"}""");

        var services = new ServiceCollection();
        services.AddNuCode(options =>
        {
            options.WorkingDirectory = _tempDir;
        });

        using var provider = services.BuildServiceProvider();
        var monitor = provider.GetRequiredService<IOptionsMonitor<NuCodeConfig>>();

        var initial = monitor.CurrentValue;
        initial.Model.ShouldBe("initial");

        // Set up change listener
        var changed = new ManualResetEventSlim(false);
        NuCodeConfig? updatedConfig = null;

        using var registration = monitor.OnChange(config =>
        {
            updatedConfig = config;
            changed.Set();
        });

        // Modify the config file
        Thread.Sleep(100); // ensure file timestamp differs
        File.WriteAllText(configPath, """{"model": "updated"}""");

        // Wait for the change notification
        var notified = changed.Wait(TimeSpan.FromSeconds(5));

        notified.ShouldBeTrue("IOptionsMonitor did not fire change notification within timeout");
        updatedConfig.ShouldNotBeNull();
        updatedConfig.Model.ShouldBe("updated");
    }

    [Fact]
    public void DiRegistersIConfigLoader()
    {
        var services = new ServiceCollection();
        services.AddNuCode(options =>
        {
            options.WorkingDirectory = _tempDir;
        });

        using var provider = services.BuildServiceProvider();
        var loader = provider.GetRequiredService<IConfigLoader>();

        loader.ShouldNotBeNull();
        loader.ShouldBeOfType<ConfigLoader>();
    }

    [Fact]
    public void DiConfigLoaderIsSingleton()
    {
        var services = new ServiceCollection();
        services.AddNuCode(options =>
        {
            options.WorkingDirectory = _tempDir;
        });

        using var provider = services.BuildServiceProvider();
        var loader1 = provider.GetRequiredService<IConfigLoader>();
        var loader2 = provider.GetRequiredService<IConfigLoader>();

        loader2.ShouldBeSameAs(loader1);
    }

    [Fact]
    public void DiNoConfigReturnsEmptyConfig()
    {
        var services = new ServiceCollection();
        services.AddNuCode(options =>
        {
            options.WorkingDirectory = _tempDir;
        });

        using var provider = services.BuildServiceProvider();
        var config = provider.GetRequiredService<IOptions<NuCodeConfig>>().Value;

        config.ShouldNotBeNull();
        config.Model.ShouldBeNull();
        config.Agents.ShouldBeNull();
        config.Permission.ShouldBeNull();
    }
}

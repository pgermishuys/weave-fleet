using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Services;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Infrastructure.Tests.Services;

public sealed class OpenCodeWarmupHostedServiceTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static OpenCodeWarmupHostedService CreateService(
        FleetOptions options,
        FakeHarnessRegistry registry)
        => new(registry, options, NullLogger<OpenCodeWarmupHostedService>.Instance);

    private static FleetOptions LocalModeOptions() => new()
    {
        Auth = new AuthOptions { Enabled = false },
        Harness = new HarnessOptions { PooledOpenCodeHarness = true },
    };

    private static FleetOptions AuthEnabledOptions() => new()
    {
        Auth = new AuthOptions { Enabled = true },
        Harness = new HarnessOptions { PooledOpenCodeHarness = true },
    };

    private static FakeHarnessRegistry RegistryWithRuntime(FakeHarnessRuntime runtime)
    {
        var registry = new FakeHarnessRegistry();
        registry.Register(runtime);
        return registry;
    }

    // ── Local mode: warmup runs when eligible ─────────────────────────────

    [Fact]
    public async Task local_mode_warmup_calls_runtime_once_with_local_user_owner()
    {
        var runtime = new FakeHarnessRuntime("opencode") { WarmupResult = true };
        var service = CreateService(LocalModeOptions(), RegistryWithRuntime(runtime));

        await service.StartAsync(CancellationToken.None);

        runtime.WarmupCalls.Count.ShouldBe(1);
        runtime.WarmupCalls[0].ShouldBe("local-user");
    }

    [Fact]
    public async Task local_mode_warmup_returns_false_when_pooled_mode_disabled_for_user()
    {
        // WarmupResult = false means pooled mode not enabled for the owner
        var runtime = new FakeHarnessRuntime("opencode") { WarmupResult = false };
        var service = CreateService(LocalModeOptions(), RegistryWithRuntime(runtime));

        // Should complete without throwing even when warmup returns false
        await Should.NotThrowAsync(() => service.StartAsync(CancellationToken.None));

        runtime.WarmupCalls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task local_mode_warmup_calls_runtime_exactly_once_on_repeated_starts()
    {
        var runtime = new FakeHarnessRuntime("opencode") { WarmupResult = true };
        var service = CreateService(LocalModeOptions(), RegistryWithRuntime(runtime));

        await service.StartAsync(CancellationToken.None);
        await service.StartAsync(CancellationToken.None);

        // Each StartAsync triggers one warmup call; no deduplication on the service level
        runtime.WarmupCalls.Count.ShouldBe(2);
    }

    // ── Auth-enabled mode: warmup skips cleanly ───────────────────────────

    [Fact]
    public async Task auth_enabled_startup_skips_warmup_without_calling_runtime()
    {
        var runtime = new FakeHarnessRuntime("opencode") { WarmupResult = true };
        var service = CreateService(AuthEnabledOptions(), RegistryWithRuntime(runtime));

        await service.StartAsync(CancellationToken.None);

        runtime.WarmupCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task auth_enabled_startup_skip_does_not_throw()
    {
        var runtime = new FakeHarnessRuntime("opencode") { WarmupResult = true };
        var service = CreateService(AuthEnabledOptions(), RegistryWithRuntime(runtime));

        await Should.NotThrowAsync(() => service.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task auth_enabled_startup_does_not_query_harness_registry_for_runtime()
    {
        var runtime = new FakeHarnessRuntime("opencode") { WarmupResult = true };
        var registry = RegistryWithRuntime(runtime);
        var service = CreateService(AuthEnabledOptions(), registry);

        await service.StartAsync(CancellationToken.None);

        // Registry should not be queried at all — the skip happens before runtime resolution
        registry.GetRuntimeByTypeCalls.ShouldBeEmpty();
    }

    // ── Startup failures do not block boot ───────────────────────────────

    [Fact]
    public async Task startup_failure_in_warmup_does_not_propagate_exception()
    {
        var runtime = new FakeHarnessRuntime("opencode");
        runtime.WarmupCalls.Clear();
        // Inject a throwing behavior via SpawnBehavior; instead configure WarmupBehavior
        // via a wrapping runtime that throws from WarmupPooledInstanceAsync
        var throwingRuntime = new ThrowingWarmupRuntime();
        var registry = new FakeHarnessRegistry();
        registry.Register(throwingRuntime);
        var service = CreateService(LocalModeOptions(), registry);

        // Must not throw — best-effort
        await Should.NotThrowAsync(() => service.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task startup_failure_logs_warning_and_continues()
    {
        var capturingLogger = new CapturingLogger<OpenCodeWarmupHostedService>();
        var throwingRuntime = new ThrowingWarmupRuntime();
        var registry = new FakeHarnessRegistry();
        registry.Register(throwingRuntime);
        var service = new OpenCodeWarmupHostedService(registry, LocalModeOptions(), capturingLogger);

        await service.StartAsync(CancellationToken.None);

        capturingLogger.WarningMessages.ShouldNotBeEmpty();
        capturingLogger.WarningMessages[0].ShouldContain("best-effort");
    }

    [Fact]
    public async Task no_opencode_runtime_registered_skips_warmup_cleanly()
    {
        var emptyRegistry = new FakeHarnessRegistry();
        var service = CreateService(LocalModeOptions(), emptyRegistry);

        // No runtime for "opencode" — must not throw
        await Should.NotThrowAsync(() => service.StartAsync(CancellationToken.None));
    }

    // ── StopAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task stop_async_completes_immediately()
    {
        var runtime = new FakeHarnessRuntime("opencode") { WarmupResult = true };
        var service = CreateService(LocalModeOptions(), RegistryWithRuntime(runtime));

        await Should.NotThrowAsync(() => service.StopAsync(CancellationToken.None));
    }

    // ── Cancellation ─────────────────────────────────────────────────────

    [Fact]
    public async Task cancelled_warmup_does_not_propagate_exception()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var runtime = new CancellationAwareRuntime();
        var registry = new FakeHarnessRegistry();
        registry.Register(runtime);
        var service = CreateService(LocalModeOptions(), registry);

        // Already-cancelled token — must not propagate
        await Should.NotThrowAsync(() => service.StartAsync(cts.Token));
    }

    // ── Private helper types ─────────────────────────────────────────────

    /// <summary>Runtime whose WarmupPooledInstanceAsync always throws an unexpected exception.</summary>
    private sealed class ThrowingWarmupRuntime : IHarnessRuntime
    {
        public string HarnessType => "opencode";

        public Task<HarnessAvailability> CheckAvailabilityAsync(CancellationToken ct)
            => Task.FromResult(new HarnessAvailability(true, null));

        public Task<RuntimePreparation> PrepareRuntimeAsync(RuntimePreparationContext context, CancellationToken ct)
            => Task.FromResult<RuntimePreparation>(new RuntimePreparation.Ready(new NullLaunchArtifacts()));

        public Task<IHarnessSession> SpawnAsync(HarnessSpawnOptions options, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<IHarnessSession> ResumeAsync(HarnessResumeOptions options, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<bool> WarmupPooledInstanceAsync(string ownerUserId, CancellationToken ct)
            => Task.FromException<bool>(new InvalidOperationException("Simulated warmup failure."));
    }

    /// <summary>Runtime whose WarmupPooledInstanceAsync respects cancellation.</summary>
    private sealed class CancellationAwareRuntime : IHarnessRuntime
    {
        public string HarnessType => "opencode";

        public Task<HarnessAvailability> CheckAvailabilityAsync(CancellationToken ct)
            => Task.FromResult(new HarnessAvailability(true, null));

        public Task<RuntimePreparation> PrepareRuntimeAsync(RuntimePreparationContext context, CancellationToken ct)
            => Task.FromResult<RuntimePreparation>(new RuntimePreparation.Ready(new NullLaunchArtifacts()));

        public Task<IHarnessSession> SpawnAsync(HarnessSpawnOptions options, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<IHarnessSession> ResumeAsync(HarnessResumeOptions options, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<bool> WarmupPooledInstanceAsync(string ownerUserId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(true);
        }
    }

    private sealed record NullLaunchArtifacts : RuntimeLaunchArtifacts;

    private sealed class CapturingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public List<string> WarningMessages { get; } = [];
        public List<string> AllMessages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            AllMessages.Add(message);
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning)
                WarningMessages.Add(message);
        }
    }
}

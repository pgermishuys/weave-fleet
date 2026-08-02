using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;
using WeaveFleet.Infrastructure.Harnesses.OpenCode.Pooling;
using WeaveFleet.Infrastructure.Services;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode.Pooling;

public sealed class PooledOpenCodeInstanceRegistryCrashIdleTests
{
    [Fact]
    public async Task crash_clears_activity_state_for_each_bound_session()
    {
        // Arrange
        var factory = new TestInstanceFactory();
        var bindingTable = new PoolDemuxBindingTable();
        var broadcaster = new FakeEventBroadcaster();
        var activityTracker = new SessionActivityTracker();
        var sessionRepo = new InMemorySessionRepository();
        
        // Seed sessions in the repository
        sessionRepo.Seed(new Session { Id = "fleet-session-1", WorkspaceId = "ws-1", UserId = "user-1" });
        sessionRepo.Seed(new Session { Id = "fleet-session-2", WorkspaceId = "ws-1", UserId = "user-1" });
        sessionRepo.Seed(new Session { Id = "fleet-session-3", WorkspaceId = "ws-1", UserId = "user-2" });
        
        var scopeFactory = CreateScopeFactory(sessionRepo);

        await using var registry = new PooledOpenCodeInstanceRegistry(
            factory.CreateWithContextAsync,
            TimeSpan.FromMinutes(1),
            NullLogger<PooledOpenCodeInstanceRegistry>.Instance,
            bindingTable,
            broadcaster,
            activityTracker,
            scopeFactory);

        // Acquire a lease and get the instance
        var lease = await registry.AcquireAsync("credential-hash", CancellationToken.None);
        var instance = lease.Instance;

        // Register bindings for multiple sessions on this instance
        bindingTable.Bind(instance, "oc-session-1", Guid.NewGuid(), "fleet-session-1", "user-1", "/repo/one", leaseGeneration: 1);
        bindingTable.Bind(instance, "oc-session-2", Guid.NewGuid(), "fleet-session-2", "user-1", "/repo/one", leaseGeneration: 1);
        bindingTable.Bind(instance, "oc-session-3", Guid.NewGuid(), "fleet-session-3", "user-2", "/repo/one", leaseGeneration: 1);

        // Verify bindings are registered before crash
        var bindingsBeforeCrash = bindingTable.GetBindingsForInstance(instance);
        bindingsBeforeCrash.Count.ShouldBe(3, "Bindings should be registered before crash");

        // Mark sessions as busy in the activity tracker
        activityTracker.Update("fleet-session-1", "busy", "user-1");
        activityTracker.Update("fleet-session-2", "busy", "user-1");
        activityTracker.Update("fleet-session-3", "busy", "user-2");

        // Act: Trigger a crash
        await instance.ReportCrashAsync(new InvalidOperationException("process crashed"));

        // Wait for crash handling to complete (replacement lease becomes available)
        var replacement = await lease.Replacement.WaitAsync(TimeSpan.FromSeconds(5));

        // Give a small delay for async broadcast operations to complete
        await Task.Delay(200);

        // Assert: Activity tracker should have cleared all bound sessions
        // This proves that BroadcastIdleForCrashedInstanceAsync was called and processed each binding
        var session1State = activityTracker.Get("fleet-session-1");
        var session2State = activityTracker.Get("fleet-session-2");
        var session3State = activityTracker.Get("fleet-session-3");
        
        session1State.ShouldBeNull("Activity tracker should have cleared fleet-session-1");
        session2State.ShouldBeNull("Activity tracker should have cleared fleet-session-2");
        session3State.ShouldBeNull("Activity tracker should have cleared fleet-session-3");

        // Assert: Bindings should have been processed (the method iterates over them)
        var bindingsAfterCrash = bindingTable.GetBindingsForInstance(instance);
        bindingsAfterCrash.Count.ShouldBe(3, "Bindings should still exist after crash (they're not removed by the crash handler)");

        await replacement.DisposeAsync();
    }

    [Fact]
    public async Task crash_with_no_bindings_does_not_broadcast()
    {
        // Arrange
        var factory = new TestInstanceFactory();
        var bindingTable = new PoolDemuxBindingTable();
        var broadcaster = new FakeEventBroadcaster();
        var activityTracker = new SessionActivityTracker();
        var scopeFactory = CreateScopeFactory();

        await using var registry = new PooledOpenCodeInstanceRegistry(
            factory.CreateWithContextAsync,
            TimeSpan.FromMinutes(1),
            NullLogger<PooledOpenCodeInstanceRegistry>.Instance,
            bindingTable,
            broadcaster,
            activityTracker,
            scopeFactory);

        // Acquire a lease but don't register any bindings
        var lease = await registry.AcquireAsync("credential-hash", CancellationToken.None);
        var instance = lease.Instance;

        // Act: Trigger a crash
        await instance.ReportCrashAsync(new InvalidOperationException("process crashed"));

        // Wait for crash handling to complete
        var replacement = await lease.Replacement.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert: No broadcasts should have been sent
        broadcaster.Broadcasts.Count(b => b.Topic == "sessions" && b.Type == "activity_status").ShouldBe(0);

        await replacement.DisposeAsync();
    }

    [Fact]
    public async Task crash_without_dependencies_does_not_throw()
    {
        // Arrange: Create registry without the optional dependencies
        var factory = new TestInstanceFactory();

        await using var registry = new PooledOpenCodeInstanceRegistry(
            factory.CreateWithContextAsync,
            TimeSpan.FromMinutes(1),
            NullLogger<PooledOpenCodeInstanceRegistry>.Instance);

        // Acquire a lease
        var lease = await registry.AcquireAsync("credential-hash", CancellationToken.None);
        var instance = lease.Instance;

        // Act: Trigger a crash (should not throw even without dependencies)
        await instance.ReportCrashAsync(new InvalidOperationException("process crashed"));

        // Wait for crash handling to complete
        var replacement = await lease.Replacement.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert: Crash handling completed successfully
        replacement.Instance.ShouldNotBeSameAs(instance);
        replacement.Instance.IsAvailable.ShouldBeTrue();

        await replacement.DisposeAsync();
    }

    [Fact]
    public async Task crash_clears_prompt_trace_context_for_bound_sessions()
    {
        // Arrange
        var factory = new TestInstanceFactory();
        var bindingTable = new PoolDemuxBindingTable();
        var broadcaster = new FakeEventBroadcaster();
        var activityTracker = new SessionActivityTracker();
        var scopeFactory = CreateScopeFactory();

        await using var registry = new PooledOpenCodeInstanceRegistry(
            factory.CreateWithContextAsync,
            TimeSpan.FromMinutes(1),
            NullLogger<PooledOpenCodeInstanceRegistry>.Instance,
            bindingTable,
            broadcaster,
            activityTracker,
            scopeFactory);

        var lease = await registry.AcquireAsync("credential-hash", CancellationToken.None);
        var instance = lease.Instance;

        // Register binding and set prompt trace context
        bindingTable.Bind(instance, "oc-session-1", Guid.NewGuid(), "fleet-session-1", "user-1", "/repo/one", leaseGeneration: 1);
        var traceContext = new ActivityContext(
            ActivityTraceId.CreateFromString("00000000000000000000000000000123".AsSpan()),
            ActivitySpanId.CreateFromString("0000000000000456".AsSpan()),
            ActivityTraceFlags.Recorded);
        activityTracker.SetPromptTraceContext("fleet-session-1", traceContext);

        // Verify trace context is set
        var retrievedContext = activityTracker.GetPromptTraceContext("fleet-session-1");
        retrievedContext.ShouldNotBeNull();
        retrievedContext.Value.TraceId.ShouldBe(traceContext.TraceId);

        // Act: Trigger a crash
        await instance.ReportCrashAsync(new InvalidOperationException("process crashed"));
        var replacement = await lease.Replacement.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert: Trace context should be cleared
        activityTracker.GetPromptTraceContext("fleet-session-1").ShouldBeNull();

        await replacement.DisposeAsync();
    }

    private static IServiceScopeFactory CreateScopeFactory(InMemorySessionRepository? sessionRepo = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISessionRepository>(sp => sessionRepo ?? new InMemorySessionRepository());
        services.AddSingleton<SessionCapabilitiesResolver>();
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private sealed class TestInstanceFactory
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _shutdowns = new(StringComparer.Ordinal);
        private int _spawnCount;

        public int SpawnCount => Volatile.Read(ref _spawnCount);

        public Task<PooledOpenCodeInstance> CreateWithContextAsync(
            string key,
            string directory,
            IReadOnlyDictionary<string, string> environment,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var number = Interlocked.Increment(ref _spawnCount);
            var instanceId = $"instance-{number}";
            var shutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _shutdowns[instanceId] = shutdown;

            var instance = new PooledOpenCodeInstance(
                key,
                instanceId,
                processId: number,
                shutdownAsync: () =>
                {
                    shutdown.TrySetResult();
                    return ValueTask.CompletedTask;
                });

            return Task.FromResult(instance);
        }
    }
}

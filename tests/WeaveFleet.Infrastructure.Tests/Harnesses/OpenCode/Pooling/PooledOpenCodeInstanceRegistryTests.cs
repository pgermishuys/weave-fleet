using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Diagnostics;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;
using WeaveFleet.Infrastructure.Harnesses.OpenCode.Pooling;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode.Pooling;

public sealed class PooledOpenCodeInstanceRegistryTests
{
    [Fact]
    public async Task concurrent_acquire_returns_same_instance()
    {
        var factory = new TestInstanceFactory();
        await using var registry = CreateRegistry(factory, TimeSpan.FromMinutes(1));

        var acquireTasks = Enumerable.Range(0, 16)
            .Select(_ => registry.AcquireAsync("credential-hash", CancellationToken.None))
            .ToArray();

        var leases = await Task.WhenAll(acquireTasks);

        leases.Select(lease => lease.Instance.InstanceId).Distinct().Count().ShouldBe(1);
        factory.SpawnCount.ShouldBe(1);

        foreach (var lease in leases)
        {
            await lease.DisposeAsync();
        }
    }

    [Fact]
    public async Task same_authoritative_environment_reuses_same_instance()
    {
        var factory = new TestInstanceFactory();
        await using var registry = CreateRegistry(factory, TimeSpan.FromMinutes(1));
        var firstEnvironment = CreateEnvironment("same-key", "enabled");
        var secondEnvironment = CreateEnvironment("same-key", "enabled");

        var firstLease = await registry.AcquireAsync(firstEnvironment, Directory.GetCurrentDirectory(), CancellationToken.None);
        var secondLease = await registry.AcquireAsync(secondEnvironment, Directory.GetCurrentDirectory(), CancellationToken.None);

        try
        {
            secondLease.Instance.ShouldBeSameAs(firstLease.Instance);
            factory.SpawnCount.ShouldBe(1);
        }
        finally
        {
            await secondLease.DisposeAsync();
            await firstLease.DisposeAsync();
        }
    }

    [Fact]
    public async Task different_api_keys_use_different_instances()
    {
        var factory = new TestInstanceFactory();
        await using var registry = CreateRegistry(factory, TimeSpan.FromMinutes(1));

        var firstLease = await registry.AcquireAsync(
            CreateEnvironment("first-key", "enabled"),
            Directory.GetCurrentDirectory(),
            CancellationToken.None);
        var secondLease = await registry.AcquireAsync(
            CreateEnvironment("second-key", "enabled"),
            Directory.GetCurrentDirectory(),
            CancellationToken.None);

        try
        {
            secondLease.Instance.ShouldNotBeSameAs(firstLease.Instance);
            secondLease.Instance.InstanceId.ShouldNotBe(firstLease.Instance.InstanceId);
            factory.SpawnCount.ShouldBe(2);
        }
        finally
        {
            await secondLease.DisposeAsync();
            await firstLease.DisposeAsync();
        }
    }

    [Fact]
    public async Task different_non_credential_environment_values_use_different_instances()
    {
        var factory = new TestInstanceFactory();
        await using var registry = CreateRegistry(factory, TimeSpan.FromMinutes(1));

        var firstLease = await registry.AcquireAsync(
            CreateEnvironment("same-key", "enabled"),
            Directory.GetCurrentDirectory(),
            CancellationToken.None);
        var secondLease = await registry.AcquireAsync(
            CreateEnvironment("same-key", "disabled"),
            Directory.GetCurrentDirectory(),
            CancellationToken.None);

        try
        {
            secondLease.Instance.ShouldNotBeSameAs(firstLease.Instance);
            factory.SpawnCount.ShouldBe(2);
        }
        finally
        {
            await secondLease.DisposeAsync();
            await firstLease.DisposeAsync();
        }
    }

    [Fact]
    public void credential_hasher_does_not_retain_plaintext_credentials_after_hash_returned()
    {
        var secret = $"plaintext-api-key-that-must-not-be-retained-{Guid.NewGuid():N}";
        var environment = CreateEnvironment(secret, "enabled");

        var hash = CredentialHasher.HashEnvironment(environment);
        var fields = typeof(CredentialHasher).GetFields(BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        var properties = typeof(CredentialHasher).GetProperties(BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        var events = typeof(CredentialHasher).GetEvents(BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        hash.ShouldNotContain(secret);
        hash.ShouldNotContain("ANTHROPIC_API_KEY");
        hash.ShouldNotContain("OPENCODE_FEATURE_FLAG");
        hash.ShouldNotContain("enabled");
        fields.ShouldBeEmpty();
        properties.ShouldBeEmpty();
        events.ShouldBeEmpty();
    }

    [Fact]
    public void credential_hasher_returns_same_hash_for_same_environment_and_changes_for_different_credentials()
    {
        var secret = $"stable-plaintext-api-key-{Guid.NewGuid():N}";
        var firstEnvironment = CreateEnvironment(secret, "enabled");
        var sameEnvironment = CreateEnvironment(secret, "enabled");
        var differentCredentialEnvironment = CreateEnvironment($"rotated-plaintext-api-key-{Guid.NewGuid():N}", "enabled");

        var firstHash = CredentialHasher.HashEnvironment(firstEnvironment);
        var sameHash = CredentialHasher.HashEnvironment(sameEnvironment);
        var differentCredentialHash = CredentialHasher.HashEnvironment(differentCredentialEnvironment);

        sameHash.ShouldBe(firstHash);
        differentCredentialHash.ShouldNotBe(firstHash);
    }

    [Fact]
    public async Task last_release_keeps_process_alive_until_idle_ttl_then_shuts_down()
    {
        var factory = new TestInstanceFactory();
        await using var registry = CreateRegistry(factory, TimeSpan.FromMilliseconds(150));

        var lease = await registry.AcquireAsync("credential-hash", CancellationToken.None);
        var instance = lease.Instance;

        await lease.DisposeAsync();

        var stoppedBeforeTtl = await factory.WaitForShutdownAsync(instance.InstanceId, TimeSpan.FromMilliseconds(50));
        stoppedBeforeTtl.ShouldBeFalse();

        var stopped = await factory.WaitForShutdownAsync(instance.InstanceId, TimeSpan.FromSeconds(5));

        stopped.ShouldBeTrue();
        factory.ShutdownCount.ShouldBe(1);
    }

    [Fact]
    public async Task acquire_during_idle_ttl_cancels_shutdown()
    {
        var factory = new TestInstanceFactory();
        await using var registry = CreateRegistry(factory, TimeSpan.FromMilliseconds(150));

        var firstLease = await registry.AcquireAsync("credential-hash", CancellationToken.None);
        var instance = firstLease.Instance;
        await firstLease.DisposeAsync();

        var secondLease = await registry.AcquireAsync("credential-hash", CancellationToken.None);

        secondLease.Instance.ShouldBeSameAs(instance);
        factory.SpawnCount.ShouldBe(1);

        var stopped = await factory.WaitForShutdownAsync(instance.InstanceId, TimeSpan.FromMilliseconds(250));

        stopped.ShouldBeFalse();
        factory.ShutdownCount.ShouldBe(0);

        await secondLease.DisposeAsync();
    }

    [Fact]
    public async Task explicit_stop_of_last_lease_shuts_down_immediately_without_idle_ttl()
    {
        var factory = new TestInstanceFactory();
        await using var registry = CreateRegistry(factory, TimeSpan.FromMinutes(1));

        var lease = await registry.AcquireAsync("credential-hash", CancellationToken.None);
        var instance = lease.Instance;

        await lease.StopAsync();

        var stopped = await factory.WaitForShutdownAsync(instance.InstanceId, TimeSpan.FromSeconds(5));

        stopped.ShouldBeTrue();
        factory.ShutdownCount.ShouldBe(1);
    }

    [Fact]
    public async Task double_release_is_ignored_and_does_not_underflow_ref_count()
    {
        var factory = new TestInstanceFactory();
        await using var registry = CreateRegistry(factory, TimeSpan.FromMilliseconds(100));

        var firstLease = await registry.AcquireAsync("credential-hash", CancellationToken.None);
        var secondLease = await registry.AcquireAsync("credential-hash", CancellationToken.None);
        var instance = firstLease.Instance;

        await firstLease.DisposeAsync();
        await firstLease.DisposeAsync();

        var stoppedWhileSecondLeaseActive = await factory.WaitForShutdownAsync(instance.InstanceId, TimeSpan.FromMilliseconds(250));
        stoppedWhileSecondLeaseActive.ShouldBeFalse();

        await secondLease.DisposeAsync();

        var stoppedAfterLastRelease = await factory.WaitForShutdownAsync(instance.InstanceId, TimeSpan.FromSeconds(5));
        stoppedAfterLastRelease.ShouldBeTrue();
        factory.ShutdownCount.ShouldBe(1);
    }

    [Fact]
    public async Task immediate_release_of_one_of_multiple_leases_keeps_instance_until_last_lease_releases()
    {
        var factory = new TestInstanceFactory();
        await using var registry = CreateRegistry(factory, TimeSpan.FromMinutes(1));

        var firstLease = await registry.AcquireAsync("credential-hash", CancellationToken.None);
        var secondLease = await registry.AcquireAsync("credential-hash", CancellationToken.None);
        var instance = firstLease.Instance;

        await firstLease.StopAsync();

        var stoppedBeforeLastRelease = await factory.WaitForShutdownAsync(instance.InstanceId, TimeSpan.FromMilliseconds(150));
        stoppedBeforeLastRelease.ShouldBeFalse();

        await secondLease.StopAsync();

        var stoppedAfterLastRelease = await factory.WaitForShutdownAsync(instance.InstanceId, TimeSpan.FromSeconds(5));
        stoppedAfterLastRelease.ShouldBeTrue();
        factory.ShutdownCount.ShouldBe(1);
    }

    [Fact]
    public async Task acquire_after_registry_dispose_throws_object_disposed()
    {
        var factory = new TestInstanceFactory();
        var registry = CreateRegistry(factory, TimeSpan.FromMinutes(1));

        await registry.DisposeAsync();

        await Should.ThrowAsync<ObjectDisposedException>(() => registry.AcquireAsync("credential-hash", CancellationToken.None));
        factory.SpawnCount.ShouldBe(0);
    }

    [Fact]
    public async Task registry_dispose_stops_active_instance_and_is_idempotent()
    {
        var factory = new TestInstanceFactory();
        var registry = CreateRegistry(factory, TimeSpan.FromMinutes(1));
        var lease = await registry.AcquireAsync("credential-hash", CancellationToken.None);
        var instance = lease.Instance;

        await registry.DisposeAsync();
        await registry.DisposeAsync();

        var stopped = await factory.WaitForShutdownAsync(instance.InstanceId, TimeSpan.FromSeconds(5));
        stopped.ShouldBeTrue();
        factory.ShutdownCount.ShouldBe(1);
    }

    [Fact]
    public async Task lease_release_after_registry_dispose_is_ignored()
    {
        var factory = new TestInstanceFactory();
        var registry = CreateRegistry(factory, TimeSpan.FromMinutes(1));
        var lease = await registry.AcquireAsync("credential-hash", CancellationToken.None);

        await registry.DisposeAsync();
        await lease.DisposeAsync();

        lease.IsReleased.ShouldBeTrue();
        factory.ShutdownCount.ShouldBe(1);
    }

    [Fact]
    public async Task standalone_instance_crash_without_registry_marks_unavailable_and_rejects_new_leases()
    {
        var instance = new PooledOpenCodeInstance(
            "credential-hash",
            "standalone-instance",
            processId: 123,
            shutdownAsync: () => ValueTask.CompletedTask);

        await instance.ReportCrashAsync(new InvalidOperationException("process crashed"));
        await instance.ReportCrashAsync(new InvalidOperationException("ignored duplicate crash"));

        instance.IsFaulted.ShouldBeTrue();
        instance.IsAvailable.ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => instance.CreateLease(static (_, _) => ValueTask.CompletedTask))
            .Message.ShouldContain("faulted");
    }

    [Fact]
    public void constructors_reject_invalid_arguments()
    {
        var factory = new TestInstanceFactory();

        Should.Throw<ArgumentNullException>(() => new PooledOpenCodeInstanceRegistry(
            (Func<string, CancellationToken, Task<PooledOpenCodeInstance>>)null!,
            TimeSpan.FromMinutes(1),
            NullLogger<PooledOpenCodeInstanceRegistry>.Instance));
        Should.Throw<ArgumentOutOfRangeException>(() => new PooledOpenCodeInstanceRegistry(
            factory.CreateAsync,
            TimeSpan.FromMilliseconds(-1),
            NullLogger<PooledOpenCodeInstanceRegistry>.Instance));
        Should.Throw<ArgumentNullException>(() => new PooledOpenCodeInstanceRegistry(
            factory.CreateAsync,
            TimeSpan.FromMinutes(1),
            null!));
        Should.Throw<ArgumentNullException>(() => new PooledOpenCodeInstanceRegistry(
            (key, directory, ct) => factory.CreateWithDirectoryAsync(key, directory, ct),
            TimeSpan.FromMinutes(1),
            null!));
        Should.Throw<ArgumentNullException>(() => new PooledOpenCodeInstanceRegistry(
            (Func<string, string, IReadOnlyDictionary<string, string>, CancellationToken, Task<PooledOpenCodeInstance>>)null!,
            TimeSpan.FromMinutes(1),
            NullLogger<PooledOpenCodeInstanceRegistry>.Instance));
    }

    [Fact]
    public async Task mismatched_authoritative_environment_hash_is_rejected_before_spawn()
    {
        var factory = new TestInstanceFactory();
        await using var registry = CreateRegistry(factory, TimeSpan.FromMinutes(1));
        var environment = CreateEnvironment("actual-key", "enabled");

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => registry.AcquireAsync(
            "wrong-hash",
            environment,
            Directory.GetCurrentDirectory(),
            CancellationToken.None));

        exception.Message.ShouldContain("credential hash");
        factory.SpawnCount.ShouldBe(0);
    }

    [Fact]
    public async Task factory_returning_wrong_key_is_disposed_and_rejected()
    {
        var shutdowns = 0;
        await using var registry = new PooledOpenCodeInstanceRegistry(
            (key, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(new PooledOpenCodeInstance(
                    $"{key}-wrong",
                    "wrong-instance",
                    processId: 456,
                    shutdownAsync: () =>
                    {
                        shutdowns++;
                        return ValueTask.CompletedTask;
                    }));
            },
            TimeSpan.FromMinutes(1),
            NullLogger<PooledOpenCodeInstanceRegistry>.Instance);

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => registry.AcquireAsync("credential-hash", CancellationToken.None));

        exception.Message.ShouldContain("wrong key");
        shutdowns.ShouldBe(1);
    }

    [Fact]
    public async Task crash_propagates_to_all_lessees()
    {
        var factory = new TestInstanceFactory();
        await using var registry = CreateRegistry(factory, TimeSpan.FromMinutes(1));

        var firstLease = await registry.AcquireAsync("credential-hash", CancellationToken.None);
        var secondLease = await registry.AcquireAsync("credential-hash", CancellationToken.None);
        var crashedInstance = firstLease.Instance;
        var crash = new InvalidOperationException("process crashed");

        await crashedInstance.ReportCrashAsync(crash);

        var firstFault = await firstLease.Faulted.WaitAsync(TimeSpan.FromSeconds(5));
        var secondFault = await secondLease.Faulted.WaitAsync(TimeSpan.FromSeconds(5));

        firstFault.ShouldBeSameAs(crash);
        secondFault.ShouldBeSameAs(crash);
        factory.SpawnCount.ShouldBe(2);

        await firstLease.DisposeAsync();
        await secondLease.DisposeAsync();
    }

    [Fact]
    public async Task crash_replaces_active_lease_for_next_operation()
    {
        var factory = new TestInstanceFactory();
        await using var registry = CreateRegistry(factory, TimeSpan.FromMinutes(1));
        var lease = await registry.AcquireAsync("credential-hash", CancellationToken.None);
        var crashedInstance = lease.Instance;

        await crashedInstance.ReportCrashAsync(new InvalidOperationException("process crashed"));

        var replacement = await lease.Replacement.WaitAsync(TimeSpan.FromSeconds(5));

        replacement.Instance.ShouldNotBeSameAs(crashedInstance);
        replacement.Instance.IsAvailable.ShouldBeTrue();
        factory.SpawnCount.ShouldBe(2);

        await replacement.DisposeAsync();
    }

    [Fact]
    public async Task crash_with_no_active_leases_disposes_crashed_instance_and_allows_fresh_acquire()
    {
        var factory = new TestInstanceFactory();
        await using var registry = CreateRegistry(factory, TimeSpan.FromMinutes(1));
        var lease = await registry.AcquireAsync("credential-hash", CancellationToken.None);
        var crashedInstance = lease.Instance;
        await lease.DisposeAsync();

        await crashedInstance.ReportCrashAsync(new InvalidOperationException("process crashed while idle"));

        var stopped = await factory.WaitForShutdownAsync(crashedInstance.InstanceId, TimeSpan.FromSeconds(5));
        var nextLease = await registry.AcquireAsync("credential-hash", CancellationToken.None);

        stopped.ShouldBeTrue();
        nextLease.Instance.ShouldNotBeSameAs(crashedInstance);
        factory.SpawnCount.ShouldBe(2);

        await nextLease.DisposeAsync();
    }

    [Fact]
    public async Task reporting_crash_after_registry_dispose_is_ignored()
    {
        var factory = new TestInstanceFactory();
        var registry = CreateRegistry(factory, TimeSpan.FromMinutes(1));
        var lease = await registry.AcquireAsync("credential-hash", CancellationToken.None);
        var instance = lease.Instance;

        await registry.DisposeAsync();
        await instance.ReportCrashAsync(new InvalidOperationException("process crashed after dispose"));

        factory.SpawnCount.ShouldBe(1);
    }

    [Fact]
    public async Task crash_restart_uses_original_startup_directory_and_authoritative_environment()
    {
        var factory = new TestInstanceFactory();
        await using var registry = new PooledOpenCodeInstanceRegistry(
            factory.CreateWithContextAsync,
            TimeSpan.FromMinutes(1),
            NullLogger<PooledOpenCodeInstanceRegistry>.Instance);
        var environment = CreateEnvironment("api-key", "enabled");
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "repo-one");

        var lease = await registry.AcquireAsync(environment, directory, CancellationToken.None);
        await lease.Instance.ReportCrashAsync(new InvalidOperationException("process crashed"));

        var replacement = await lease.Replacement.WaitAsync(TimeSpan.FromSeconds(5));

        factory.SpawnCount.ShouldBe(2);
        factory.Directories.ShouldBe([directory, directory]);
        factory.Environments.ShouldAllBe(captured => ReferenceEquals(captured, environment));

        await replacement.DisposeAsync();
    }

    [Fact]
    public async Task crash_restart_uses_current_directory_and_empty_environment_when_legacy_acquire_has_no_context()
    {
        var factory = new TestInstanceFactory();
        await using var registry = new PooledOpenCodeInstanceRegistry(
            factory.CreateWithContextAsync,
            TimeSpan.FromMinutes(1),
            NullLogger<PooledOpenCodeInstanceRegistry>.Instance);

        var lease = await registry.AcquireAsync("credential-hash", CancellationToken.None);
        await lease.Instance.ReportCrashAsync(new InvalidOperationException("process crashed"));

        var replacement = await lease.Replacement.WaitAsync(TimeSpan.FromSeconds(5));

        factory.Directories.ShouldBe([Environment.CurrentDirectory, Environment.CurrentDirectory]);
        factory.Environments[1].ShouldBeEmpty();

        await replacement.DisposeAsync();
    }

    [Fact]
    public async Task crash_restart_uses_environment_current_directory_when_startup_directory_is_missing()
    {
        var factory = new TestInstanceFactory();
        await using var registry = new PooledOpenCodeInstanceRegistry(
            factory.CreateWithContextAsync,
            TimeSpan.FromMinutes(1),
            NullLogger<PooledOpenCodeInstanceRegistry>.Instance);

        var lease = await registry.AcquireAsync("credential-hash", CancellationToken.None);
        SetRegistryEntryStartupDirectory(registry, "credential-hash", null);
        await lease.Instance.ReportCrashAsync(new InvalidOperationException("process crashed"));

        var replacement = await lease.Replacement.WaitAsync(TimeSpan.FromSeconds(5));

        factory.Directories[1].ShouldBe(Environment.CurrentDirectory);

        await replacement.DisposeAsync();
    }

    [Fact]
    public async Task canceled_acquire_propagates_cancellation_and_removes_empty_entry()
    {
        var factory = new TestInstanceFactory();
        await using var registry = CreateRegistry(factory, TimeSpan.FromMinutes(1));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => registry.AcquireAsync("credential-hash", cts.Token));

        var lease = await registry.AcquireAsync("credential-hash", CancellationToken.None);
        factory.SpawnCount.ShouldBe(1);
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task acquire_replaces_unavailable_instance_without_crash_restart()
    {
        var factory = new TestInstanceFactory();
        await using var registry = CreateRegistry(factory, TimeSpan.FromMinutes(1));
        var firstLease = await registry.AcquireAsync("credential-hash", CancellationToken.None);
        var unavailableInstance = firstLease.Instance;

        await unavailableInstance.DisposeAsync();

        var secondLease = await registry.AcquireAsync("credential-hash", CancellationToken.None);

        secondLease.Instance.ShouldNotBeSameAs(unavailableInstance);
        factory.SpawnCount.ShouldBe(2);

        await firstLease.DisposeAsync();
        await secondLease.DisposeAsync();
    }

    [Fact]
    public async Task crash_from_replaced_instance_is_ignored_without_disturbing_current_instance()
    {
        var factory = new TestInstanceFactory();
        await using var registry = CreateRegistry(factory, TimeSpan.FromMinutes(1));
        var firstLease = await registry.AcquireAsync("credential-hash", CancellationToken.None);
        var replacedInstance = firstLease.Instance;

        await replacedInstance.DisposeAsync();
        var secondLease = await registry.AcquireAsync("credential-hash", CancellationToken.None);

        await replacedInstance.ReportCrashAsync(new InvalidOperationException("stale process crashed"));

        secondLease.Instance.IsAvailable.ShouldBeTrue();
        secondLease.Instance.ShouldNotBeSameAs(replacedInstance);
        factory.SpawnCount.ShouldBe(2);

        await firstLease.DisposeAsync();
        await secondLease.DisposeAsync();
    }

    [Fact]
    public async Task crash_after_registry_dispose_is_ignored_because_entry_was_removed()
    {
        var factory = new TestInstanceFactory();
        var registry = CreateRegistry(factory, TimeSpan.FromMinutes(1));
        var lease = await registry.AcquireAsync("credential-hash", CancellationToken.None);
        var instance = lease.Instance;

        await registry.DisposeAsync();
        await instance.ReportCrashAsync(new InvalidOperationException("process crashed after dispose"));

        factory.SpawnCount.ShouldBe(1);
        factory.ShutdownCount.ShouldBe(1);
        lease.IsReleased.ShouldBeFalse();
    }

    [Fact]
    public async Task failing_factory_removes_empty_entry_and_allows_retry()
    {
        var attempts = 0;
        var factory = new TestInstanceFactory();
        await using var registry = new PooledOpenCodeInstanceRegistry(
            async (key, ct) =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    throw new InvalidOperationException("spawn failed");
                }

                return await factory.CreateAsync(key, ct).ConfigureAwait(false);
            },
            TimeSpan.FromMinutes(1),
            NullLogger<PooledOpenCodeInstanceRegistry>.Instance);

        await Should.ThrowAsync<InvalidOperationException>(() => registry.AcquireAsync("credential-hash", CancellationToken.None));
        var lease = await registry.AcquireAsync("credential-hash", CancellationToken.None);

        attempts.ShouldBe(2);
        factory.SpawnCount.ShouldBe(1);

        await lease.DisposeAsync();
    }

    [Fact]
    public async Task release_after_registry_dispose_removes_lease_without_throwing()
    {
        var factory = new TestInstanceFactory();
        var registry = CreateRegistry(factory, TimeSpan.FromMinutes(1));
        var lease = await registry.AcquireAsync("credential-hash", CancellationToken.None);

        await registry.DisposeAsync();
        await lease.DisposeAsync();

        lease.IsReleased.ShouldBeTrue();
    }

    [Fact]
    public async Task crash_loop_marks_active_lease_permanently_faulted_after_restart_limit()
    {
        var factory = new TestInstanceFactory();
        await using var registry = CreateRegistry(factory, TimeSpan.FromMinutes(1));
        var lease = await registry.AcquireAsync("credential-hash", CancellationToken.None);
        var current = lease;

        for (var i = 0; i < 3; i++)
        {
            await current.Instance.ReportCrashAsync(new InvalidOperationException($"process crashed {i}"));
            current = await current.Replacement.WaitAsync(TimeSpan.FromSeconds(5));
        }

        var permanentCrash = new InvalidOperationException("process crashed permanently");
        await current.Instance.ReportCrashAsync(permanentCrash);

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => current.Replacement.WaitAsync(TimeSpan.FromSeconds(5)));
        exception.ShouldBeSameAs(permanentCrash);
        factory.SpawnCount.ShouldBe(4);
    }

    [Fact]
    public async Task pool_metrics_report_active_instances_sessions_and_restarts()
    {
        var factory = new TestInstanceFactory();
        using var listener = new TestMeterListener();
        listener.Start();
        await using var registry = CreateRegistry(factory, TimeSpan.FromMinutes(1));

        var firstLease = await registry.AcquireAsync("credential-hash", CancellationToken.None);
        var secondLease = await registry.AcquireAsync("credential-hash", CancellationToken.None);

        listener.RecordObservableInstruments();

        listener.GetLatestObservableValue("opencode_pool_instances_active").ShouldBe(1);
        listener.GetObservableValues("opencode_pool_sessions_per_instance").ShouldContain(2);
        listener.GetObservableValues("opencode_pool_utilization").ShouldContain(2);

        await firstLease.Instance.ReportCrashAsync(new InvalidOperationException("process crashed"));

        listener.GetCounterValue("opencode_pool_process_restarts").ShouldBe(1);

        await firstLease.DisposeAsync();
        await secondLease.DisposeAsync();
    }

    [Fact]
    public async Task pool_operation_logs_cover_lifecycle_and_do_not_include_plaintext_credentials()
    {
        var factory = new TestInstanceFactory();
        var logger = new CapturingLogger<PooledOpenCodeInstanceRegistry>();
        await using var registry = new PooledOpenCodeInstanceRegistry(
            factory.CreateWithContextAsync,
            TimeSpan.FromMilliseconds(20),
            logger);
        const string firstSecret = "plaintext-api-key-one";
        const string secondSecret = "plaintext-api-key-two";
        var firstEnvironment = CreateEnvironment(firstSecret, "enabled");
        var secondEnvironment = CreateEnvironment(secondSecret, "enabled");
        var firstHash = CredentialHasher.HashEnvironment(firstEnvironment);
        var secondHash = CredentialHasher.HashEnvironment(secondEnvironment);

        var firstLease = await registry.AcquireAsync(firstEnvironment, Directory.GetCurrentDirectory(), CancellationToken.None);
        var secondLease = await registry.AcquireAsync(firstEnvironment, Directory.GetCurrentDirectory(), CancellationToken.None);
        var isolatedLease = await registry.AcquireAsync(secondEnvironment, Directory.GetCurrentDirectory(), CancellationToken.None);

        await firstLease.Instance.ReportCrashAsync(new InvalidOperationException("process crashed"));
        var firstReplacement = await firstLease.Replacement.WaitAsync(TimeSpan.FromSeconds(5));
        var secondReplacement = await secondLease.Replacement.WaitAsync(TimeSpan.FromSeconds(5));

        await firstReplacement.DisposeAsync();
        await secondReplacement.DisposeAsync();
        await isolatedLease.DisposeAsync();

        (await factory.WaitForShutdownAsync(firstReplacement.Instance.InstanceId, TimeSpan.FromSeconds(5))).ShouldBeTrue();
        (await factory.WaitForShutdownAsync(isolatedLease.Instance.InstanceId, TimeSpan.FromSeconds(5))).ShouldBeTrue();

        var eventNames = logger.Entries.Select(entry => entry.EventId.Name).ToArray();
        eventNames.ShouldContain("AcquireRequested");
        eventNames.ShouldContain("AcquireSucceeded");
        eventNames.ShouldContain("Spawn");
        eventNames.ShouldContain("ProcessSpawn");
        eventNames.ShouldContain("ProcessKill");
        eventNames.ShouldContain("CredentialMismatchSpawn");
        eventNames.ShouldContain("CredentialBoundaryDecision");
        eventNames.ShouldContain("IdleShutdown");
        eventNames.ShouldContain("IdleTtlScheduled");
        eventNames.ShouldContain("IdleTtlExpired");
        eventNames.ShouldContain("Crash");
        eventNames.ShouldContain("ReleaseRequested");
        eventNames.ShouldContain("RefCountChanged");

        logger.Entries.ShouldContain(entry => entry.StateValues.ContainsKey("PoolKeyFingerprint"));
        logger.Entries.ShouldContain(entry => HasStateValue(entry, "Reason", "initial_acquire"));
        logger.Entries.ShouldContain(entry => HasStateValue(entry, "Reason", "crash_restart"));
        logger.Entries.ShouldContain(entry => HasStateValue(entry, "Reason", "idle_ttl"));
        logger.Entries.ShouldContain(entry => HasStateValue(entry, "Decision", "isolated_boundary_spawn"));
        logger.Entries.ShouldContain(entry => HasStateValue(entry, "PreviousRefCount", "1")
            && HasStateValue(entry, "CurrentRefCount", "2"));

        foreach (var entry in logger.Entries)
        {
            entry.Message.ShouldNotContain(firstSecret);
            entry.Message.ShouldNotContain(secondSecret);
            entry.Message.ShouldNotContain(firstHash);
            entry.Message.ShouldNotContain(secondHash);
            foreach (var value in entry.StateValues.Values.Select(value => Convert.ToString(value, CultureInfo.InvariantCulture)))
            {
                value.ShouldNotBe(firstSecret);
                value.ShouldNotBe(secondSecret);
                value.ShouldNotBe(firstHash);
                value.ShouldNotBe(secondHash);
            }
        }
    }

    [Fact]
    public async Task pool_health_status_reports_active_instances_sessions_and_process_ids()
    {
        var factory = new TestInstanceFactory();
        await using var registry = CreateRegistry(factory, TimeSpan.FromMinutes(1));

        var firstLease = await registry.AcquireAsync("credential-hash", CancellationToken.None);
        var secondLease = await registry.AcquireAsync("credential-hash", CancellationToken.None);

        var status = registry.GetHealthStatus();

        status.InstanceCount.ShouldBe(1);
        status.SessionCount.ShouldBe(2);
        status.Instances.Count.ShouldBe(1);
        status.Instances[0].InstanceId.ShouldBe(firstLease.Instance.InstanceId);
        status.Instances[0].SessionCount.ShouldBe(2);
        status.Instances[0].ProcessId.ShouldBe(1);
        status.Instances[0].IsAvailable.ShouldBeTrue();
        status.Instances[0].IsFaulted.ShouldBeFalse();
        status.Instances[0].IsDisposed.ShouldBeFalse();

        await firstLease.DisposeAsync();
        await secondLease.DisposeAsync();
    }

    [Fact]
    public void credential_hasher_is_order_independent_case_sensitive_and_length_delimited()
    {
        var first = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["B"] = "2",
            ["A"] = "1",
        };
        var second = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["A"] = "1",
            ["B"] = "2",
        };
        var differentCase = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["a"] = "1",
            ["B"] = "2",
        };
        var ambiguousConcatenationOne = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["A"] = "BC",
        };
        var ambiguousConcatenationTwo = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AB"] = "C",
        };

        CredentialHasher.HashEnvironment(first).ShouldBe(CredentialHasher.HashEnvironment(second));
        CredentialHasher.HashEnvironment(first).ShouldNotBe(CredentialHasher.HashEnvironment(differentCase));
        CredentialHasher.HashEnvironment(ambiguousConcatenationOne).ShouldNotBe(CredentialHasher.HashEnvironment(ambiguousConcatenationTwo));
        CredentialHasher.HashEnvironment(new Dictionary<string, string>()).Length.ShouldBe(64);
    }

    [Fact]
    public async Task pooled_instance_rejects_leases_after_dispose_or_fault_and_ignores_duplicate_crash_reports()
    {
        var shutdownCount = 0;
        var instance = new PooledOpenCodeInstance(
            "key",
            "instance-1",
            processId: 123,
            shutdownAsync: () =>
            {
                shutdownCount++;
                return ValueTask.CompletedTask;
            },
            NullLogger<PooledOpenCodeInstance>.Instance);
        var lease = instance.CreateLease(static (_, _) => ValueTask.CompletedTask);
        var crash = new InvalidOperationException("process crashed");

        await instance.ReportCrashAsync(crash);
        await instance.ReportCrashAsync(new InvalidOperationException("duplicate crash"));

        (await lease.Faulted.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeSameAs(crash);
        Should.Throw<InvalidOperationException>(() => instance.CreateLease(static (_, _) => ValueTask.CompletedTask));

        await instance.DisposeAsync();
        await instance.DisposeAsync();

        shutdownCount.ShouldBe(1);
        Should.Throw<ObjectDisposedException>(() => instance.CreateLease(static (_, _) => ValueTask.CompletedTask));
    }

    [Fact]
    public async Task pooled_instance_process_exit_reports_crash_and_dispose_detaches_process_manager()
    {
        var processManager = new OpenCodeProcessManager(NullLogger<OpenCodeProcessManager>.Instance);
        var instance = new PooledOpenCodeInstance(
            "key",
            "instance-with-process-manager",
            processId: null,
            httpClient: null,
            processManager,
            shutdownAsync: () => ValueTask.CompletedTask,
            NullLogger<PooledOpenCodeInstance>.Instance);
        var lease = instance.CreateLease(static (_, _) => ValueTask.CompletedTask);
        var onProcessExited = typeof(PooledOpenCodeInstance).GetMethod(
            "OnProcessExited",
            BindingFlags.Instance | BindingFlags.NonPublic);

        onProcessExited.ShouldNotBeNull();
        onProcessExited.Invoke(instance, [processManager, 42]);

        var fault = await lease.Faulted.WaitAsync(TimeSpan.FromSeconds(5));
        fault.Message.ShouldContain("42");

        await instance.DisposeAsync();
        onProcessExited.Invoke(instance, [processManager, 43]);

        instance.IsDisposed.ShouldBeTrue();
    }

    private static PooledOpenCodeInstanceRegistry CreateRegistry(TestInstanceFactory factory, TimeSpan idleTtl)
    {
        return new PooledOpenCodeInstanceRegistry(
            factory.CreateAsync,
            idleTtl,
            NullLogger<PooledOpenCodeInstanceRegistry>.Instance);
    }

    private static Dictionary<string, string> CreateEnvironment(string apiKey, string featureFlag)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ANTHROPIC_API_KEY"] = apiKey,
            ["OPENCODE_FEATURE_FLAG"] = featureFlag,
        };
    }

    private static void SetRegistryEntryStartupDirectory(
        PooledOpenCodeInstanceRegistry registry,
        string key,
        string? directory)
    {
        var entriesField = typeof(PooledOpenCodeInstanceRegistry).GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Field _entries was not found.");
        var entries = entriesField.GetValue(registry)
            ?? throw new InvalidOperationException("Registry entries were not available.");
        var indexer = entries.GetType().GetProperty("Item")
            ?? throw new InvalidOperationException("Registry entries indexer was not found.");
        var entry = indexer.GetValue(entries, [key])
            ?? throw new InvalidOperationException("Registry entry was not found.");
        var startupDirectoryProperty = entry.GetType().GetProperty("StartupDirectory")
            ?? throw new InvalidOperationException("StartupDirectory property was not found.");
        startupDirectoryProperty.SetValue(entry, directory);
    }

    private static bool HasStateValue(CapturedLogEntry entry, string key, string expectedValue)
    {
        return entry.StateValues.TryGetValue(key, out var value)
            && string.Equals(Convert.ToString(value, CultureInfo.InvariantCulture), expectedValue, StringComparison.Ordinal);
    }

    private sealed class TestInstanceFactory
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _shutdowns = new(StringComparer.Ordinal);
        private int _spawnCount;
        private int _shutdownCount;

        public List<string> Directories { get; } = [];

        public List<IReadOnlyDictionary<string, string>> Environments { get; } = [];

        public int SpawnCount => Volatile.Read(ref _spawnCount);

        public int ShutdownCount => Volatile.Read(ref _shutdownCount);

        public Task<PooledOpenCodeInstance> CreateAsync(string key, CancellationToken ct)
        {
            return CreateCoreAsync(key, ct);
        }

        public Task<PooledOpenCodeInstance> CreateWithContextAsync(
            string key,
            string directory,
            IReadOnlyDictionary<string, string> environment,
            CancellationToken ct)
        {
            Directories.Add(directory);
            Environments.Add(environment);
            return CreateCoreAsync(key, ct);
        }

        public Task<PooledOpenCodeInstance> CreateWithDirectoryAsync(
            string key,
            string directory,
            CancellationToken ct)
        {
            Directories.Add(directory);
            return CreateCoreAsync(key, ct);
        }

        private Task<PooledOpenCodeInstance> CreateCoreAsync(string key, CancellationToken ct)
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
                    Interlocked.Increment(ref _shutdownCount);
                    shutdown.TrySetResult();
                    return ValueTask.CompletedTask;
                });

            return Task.FromResult(instance);
        }

        public async Task<bool> WaitForShutdownAsync(string instanceId, TimeSpan timeout)
        {
            if (!_shutdowns.TryGetValue(instanceId, out var shutdown))
            {
                return false;
            }

            try
            {
                await shutdown.Task.WaitAsync(timeout).ConfigureAwait(false);
                return true;
            }
            catch (TimeoutException)
            {
                return false;
            }
        }
    }

    private sealed class TestMeterListener : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentDictionary<string, List<int>> _observableValues = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, long> _counterValues = new(StringComparer.Ordinal);

        public TestMeterListener()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == FleetInstrumentation.ServiceName
                    && instrument.Name.StartsWith("opencode_pool_", StringComparison.Ordinal))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<int>((instrument, measurement, _, _) =>
            {
                _observableValues.AddOrUpdate(
                    instrument.Name,
                    _ => [measurement],
                    (_, measurements) =>
                    {
                        measurements.Add(measurement);
                        return measurements;
                    });
            });
            _listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
            {
                _counterValues.AddOrUpdate(instrument.Name, measurement, (_, current) => current + measurement);
            });
        }

        public void Start()
        {
            _listener.Start();
        }

        public void RecordObservableInstruments()
        {
            foreach (var values in _observableValues.Values)
            {
                values.Clear();
            }

            _listener.RecordObservableInstruments();
        }

        public int GetLatestObservableValue(string instrumentName)
        {
            return _observableValues[instrumentName].Last();
        }

        public List<int> GetObservableValues(string instrumentName)
        {
            return _observableValues[instrumentName];
        }

        public long GetCounterValue(string instrumentName)
        {
            return _counterValues[instrumentName];
        }

        public void Dispose()
        {
            _listener.Dispose();
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<CapturedLogEntry> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
        {
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (state is IEnumerable<KeyValuePair<string, object?>> stateValues)
            {
                foreach (var stateValue in stateValues)
                {
                    values[stateValue.Key] = stateValue.Value;
                }
            }

            Entries.Enqueue(new CapturedLogEntry(logLevel, eventId, formatter(state, exception), values));
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }

    private sealed record CapturedLogEntry(
        LogLevel LogLevel,
        EventId EventId,
        string Message,
        IReadOnlyDictionary<string, object?> StateValues);
}

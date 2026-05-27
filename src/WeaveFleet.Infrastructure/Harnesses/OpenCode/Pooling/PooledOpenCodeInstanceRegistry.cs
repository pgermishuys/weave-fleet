using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Diagnostics;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode.Pooling;

/// <summary>
/// Thread-safe registry for shared OpenCode process instances keyed by the SHA256 hash of the
/// complete resolved runtime environment dictionary.
/// </summary>
internal sealed class PooledOpenCodeInstanceRegistry : IAsyncDisposable
{
    private const int MaxRestartAttempts = 3;
    private static readonly TimeSpan RestartWindow = TimeSpan.FromSeconds(60);
    private const string SpawnReasonInitialAcquire = "initial_acquire";
    private const string SpawnReasonCrashRestart = "crash_restart";
    private const string KillReasonIdleTtl = "idle_ttl";
    private const string KillReasonImmediateRelease = "immediate_release";
    private const string KillReasonRegistryDispose = "registry_dispose";
    private const string KillReasonCrashRecovery = "crash_recovery";

    private static readonly ConcurrentDictionary<Guid, PooledOpenCodeInstanceRegistry> MetricRegistries = new();

    private static readonly ObservableGauge<int> ActiveInstancesGauge = FleetInstrumentation.Meter.CreateObservableGauge(
        "opencode_pool_instances_active",
        ObserveActiveInstances,
        "instances",
        "Active pooled OpenCode process instances.");

    private static readonly ObservableGauge<int> SessionsPerInstanceGauge = FleetInstrumentation.Meter.CreateObservableGauge(
        "opencode_pool_sessions_per_instance",
        ObserveSessionsPerInstance,
        "sessions",
        "Active Fleet leases per pooled OpenCode process instance.");

    private static readonly ObservableGauge<int> UtilizationGauge = FleetInstrumentation.Meter.CreateObservableGauge(
        "opencode_pool_utilization",
        ObservePoolUtilization,
        "sessions",
        "Active Fleet leases per pooled OpenCode process instance; values above 1 indicate successful port-sharing in pooled mode.");

    private static readonly Counter<long> ProcessRestarts = FleetInstrumentation.Meter.CreateCounter<long>(
        "opencode_pool_process_restarts",
        "restarts",
        "Pooled OpenCode process restarts after crash recovery.");

    static PooledOpenCodeInstanceRegistry()
    {
        _ = ActiveInstancesGauge;
        _ = SessionsPerInstanceGauge;
        _ = UtilizationGauge;
    }

    private static readonly Action<ILogger, string, Exception?> LogSpawn =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(1, "Spawn"),
            "Spawning pooled OpenCode instance for key fingerprint {PoolKeyFingerprint}.");

    private static readonly Action<ILogger, string, string, Exception?> LogProcessSpawn =
        LoggerMessage.Define<string, string>(LogLevel.Information, new EventId(6, "ProcessSpawn"),
            "Spawning pooled OpenCode process for key fingerprint {PoolKeyFingerprint}; reason: {Reason}.");

    private static readonly Action<ILogger, string, string, Exception?> LogProcessKill =
        LoggerMessage.Define<string, string>(LogLevel.Information, new EventId(7, "ProcessKill"),
            "Stopping pooled OpenCode process for key fingerprint {PoolKeyFingerprint}; reason: {Reason}.");

    private static readonly Action<ILogger, string, Exception?> LogCredentialMismatchSpawn =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(5, "CredentialMismatchSpawn"),
            "Spawning new pooled OpenCode instance for key fingerprint {PoolKeyFingerprint} because the credential boundary does not match existing pooled processes.");

    private static readonly Action<ILogger, string, string, Exception?> LogCredentialBoundaryDecision =
        LoggerMessage.Define<string, string>(LogLevel.Information, new EventId(8, "CredentialBoundaryDecision"),
            "Credential boundary decision for pooled OpenCode key fingerprint {PoolKeyFingerprint}: {Decision}.");

    private static readonly Action<ILogger, string, Exception?> LogIdleShutdown =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(2, "IdleShutdown"),
            "Stopping idle pooled OpenCode instance for key fingerprint {PoolKeyFingerprint}.");

    private static readonly Action<ILogger, string, double, Exception?> LogIdleTtlScheduled =
        LoggerMessage.Define<string, double>(LogLevel.Information, new EventId(9, "IdleTtlScheduled"),
            "Scheduled pooled OpenCode idle TTL for key fingerprint {PoolKeyFingerprint}; ttl_ms: {IdleTtlMilliseconds}.");

    private static readonly Action<ILogger, string, Exception?> LogIdleTtlExpired =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(10, "IdleTtlExpired"),
            "Pooled OpenCode idle TTL expired for key fingerprint {PoolKeyFingerprint}.");

    private static readonly Action<ILogger, string, Exception?> LogCrash =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(3, "Crash"),
            "Pooled OpenCode instance crashed for key fingerprint {PoolKeyFingerprint}; restarting if leases remain.");

    private static readonly Action<ILogger, string, Exception?> LogRestartLimitExceeded =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(4, "RestartLimitExceeded"),
            "Pooled OpenCode instance restart limit exceeded for key fingerprint {PoolKeyFingerprint}; active leases remain permanently faulted.");

    private static readonly Action<ILogger, string, Exception?> LogAcquireRequested =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(11, "AcquireRequested"),
            "Acquiring pooled OpenCode instance for key fingerprint {PoolKeyFingerprint}.");

    private static readonly Action<ILogger, string, Exception?> LogAcquireSucceeded =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(12, "AcquireSucceeded"),
            "Acquired pooled OpenCode instance for key fingerprint {PoolKeyFingerprint}.");

    private static readonly Action<ILogger, string, string, Exception?> LogReleaseRequested =
        LoggerMessage.Define<string, string>(LogLevel.Information, new EventId(13, "ReleaseRequested"),
            "Releasing pooled OpenCode instance for key fingerprint {PoolKeyFingerprint}; mode: {ReleaseMode}.");

    private static readonly Action<ILogger, string, int, int, string, Exception?> LogRefCountChanged =
        LoggerMessage.Define<string, int, int, string>(LogLevel.Information, new EventId(14, "RefCountChanged"),
            "Pooled OpenCode ref-count changed for key fingerprint {PoolKeyFingerprint}: {PreviousRefCount} -> {CurrentRefCount}; reason: {Reason}.");

    private readonly ConcurrentDictionary<string, RegistryEntry> _entries = new(StringComparer.Ordinal);
    private readonly Func<string, string, IReadOnlyDictionary<string, string>, CancellationToken, Task<PooledOpenCodeInstance>> _instanceFactory;
    private readonly TimeSpan _idleTtl;
    private readonly ILogger<PooledOpenCodeInstanceRegistry> _logger;
    private readonly Guid _metricRegistryId = Guid.NewGuid();
    private int _disposed;

    public PooledOpenCodeInstanceRegistry(
        Func<string, CancellationToken, Task<PooledOpenCodeInstance>> instanceFactory,
        TimeSpan idleTtl,
        ILogger<PooledOpenCodeInstanceRegistry> logger)
        : this(
            (key, _, _, ct) => instanceFactory(key, ct),
            idleTtl,
            logger)
    {
        ArgumentNullException.ThrowIfNull(instanceFactory);
    }

    public PooledOpenCodeInstanceRegistry(
        Func<string, string, CancellationToken, Task<PooledOpenCodeInstance>> instanceFactory,
        TimeSpan idleTtl,
        ILogger<PooledOpenCodeInstanceRegistry> logger)
    {
        ArgumentNullException.ThrowIfNull(instanceFactory);

        _instanceFactory = (key, directory, _, ct) => instanceFactory(key, directory, ct);
        ArgumentOutOfRangeException.ThrowIfLessThan(idleTtl, TimeSpan.Zero);

        _idleTtl = idleTtl;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        MetricRegistries[_metricRegistryId] = this;
    }

    public PooledOpenCodeInstanceRegistry(
        Func<string, string, IReadOnlyDictionary<string, string>, CancellationToken, Task<PooledOpenCodeInstance>> instanceFactory,
        TimeSpan idleTtl,
        ILogger<PooledOpenCodeInstanceRegistry> logger)
    {
        ArgumentNullException.ThrowIfNull(instanceFactory);
        ArgumentOutOfRangeException.ThrowIfLessThan(idleTtl, TimeSpan.Zero);

        _instanceFactory = instanceFactory;
        _idleTtl = idleTtl;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        MetricRegistries[_metricRegistryId] = this;
    }

    public async Task<InstanceLease> AcquireAsync(string credentialHashKey, CancellationToken ct)
    {
        return await AcquireAsync(credentialHashKey, Environment.CurrentDirectory, ct).ConfigureAwait(false);
    }

    public async Task<InstanceLease> AcquireAsync(string credentialHashKey, string directory, CancellationToken ct)
    {
        return await AcquireAsync(
            credentialHashKey,
            new Dictionary<string, string>(),
            directory,
            validateCredentialHash: false,
            ct).ConfigureAwait(false);
    }

    public async Task<InstanceLease> AcquireAsync(
        IReadOnlyDictionary<string, string> authoritativeEnvironment,
        string directory,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(authoritativeEnvironment);

        var credentialHashKey = CredentialHasher.HashEnvironment(authoritativeEnvironment);
        return await AcquireAsync(credentialHashKey, authoritativeEnvironment, directory, ct).ConfigureAwait(false);
    }

    public async Task<InstanceLease> AcquireAsync(
        string credentialHashKey,
        IReadOnlyDictionary<string, string> authoritativeEnvironment,
        string directory,
        CancellationToken ct)
    {
        return await AcquireAsync(
            credentialHashKey,
            authoritativeEnvironment,
            directory,
            validateCredentialHash: true,
            ct).ConfigureAwait(false);
    }

    private async Task<InstanceLease> AcquireAsync(
        string credentialHashKey,
        IReadOnlyDictionary<string, string> authoritativeEnvironment,
        string directory,
        bool validateCredentialHash,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialHashKey);
        ArgumentNullException.ThrowIfNull(authoritativeEnvironment);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var keyFingerprint = CreateTelemetryKeyFingerprint(credentialHashKey);
        LogAcquireRequested(_logger, keyFingerprint, null);

        if (validateCredentialHash)
        {
            var authoritativeHash = CredentialHasher.HashEnvironment(authoritativeEnvironment);
            if (!string.Equals(credentialHashKey, authoritativeHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Pooled OpenCode credential hash does not match the authoritative resolved environment.");
            }

            LogCredentialBoundaryDecision(_logger, keyFingerprint, "authoritative_hash_matched", null);
        }

        while (true)
        {
            var entry = _entries.GetOrAdd(credentialHashKey, static key => new RegistryEntry(key));

            await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                if (!_entries.TryGetValue(credentialHashKey, out var currentEntry) || !ReferenceEquals(currentEntry, entry))
                {
                    continue;
                }

                entry.CancelIdleShutdown();

                if (entry.Instance is null || !entry.Instance.IsAvailable)
                {
                    entry.StartupDirectory = directory;
                    if (HasAvailableInstanceForDifferentCredentialBoundary(credentialHashKey))
                    {
                        LogCredentialMismatchSpawn(_logger, keyFingerprint, null);
                        LogCredentialBoundaryDecision(_logger, keyFingerprint, "isolated_boundary_spawn", null);
                    }
                    else
                    {
                        LogCredentialBoundaryDecision(_logger, keyFingerprint, "new_or_recovered_boundary_spawn", null);
                    }

                    entry.Instance = await SpawnAsync(
                            entry.Key,
                            directory,
                            authoritativeEnvironment,
                            SpawnReasonInitialAcquire,
                            ct)
                        .ConfigureAwait(false);
                    entry.RestartEnvironment = authoritativeEnvironment;
                }
                else
                {
                    LogCredentialBoundaryDecision(_logger, keyFingerprint, "existing_boundary_reused", null);
                }

                var previousRefCount = entry.RefCount;
                entry.RefCount++;
                LogRefCountChanged(_logger, keyFingerprint, previousRefCount, entry.RefCount, "acquire", null);
                LogAcquireSucceeded(_logger, keyFingerprint, null);
                return entry.Instance.CreateLease(ReleaseLeaseAsync);
            }
            catch
            {
                if (entry.RefCount == 0 && entry.Instance is null)
                {
                    _entries.TryRemove(entry.Key, out _);
                }

                throw;
            }
            finally
            {
                entry.Gate.Release();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        MetricRegistries.TryRemove(_metricRegistryId, out _);

        var entries = _entries.Values.ToArray();
        _entries.Clear();

        foreach (var entry in entries)
        {
            await entry.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                entry.CancelIdleShutdown();
                if (entry.Instance is not null)
                {
                    LogProcessKill(_logger, CreateTelemetryKeyFingerprint(entry.Key), KillReasonRegistryDispose, null);
                    await entry.Instance.DisposeAsync().ConfigureAwait(false);
                    entry.Instance = null;
                    entry.RestartEnvironment = null;
                }
            }
            finally
            {
                entry.Gate.Release();
                entry.Dispose();
            }
        }
    }

    internal OpenCodePoolHealthStatus GetHealthStatus()
    {
        var instances = _entries.Values
            .Select(entry => entry.Instance is null
                ? null
                : new OpenCodePoolInstanceHealth(
                    entry.Instance.InstanceId,
                    entry.RefCount,
                    entry.Instance.ProcessId,
                    entry.Instance.IsAvailable,
                    entry.Instance.IsFaulted,
                    entry.Instance.IsDisposed))
            .OfType<OpenCodePoolInstanceHealth>()
            .ToArray();

        return new OpenCodePoolHealthStatus(
            instances.Length,
            instances.Sum(instance => instance.SessionCount),
            instances);
    }

    private async ValueTask ReleaseLeaseAsync(InstanceLease lease, InstanceLeaseReleaseMode releaseMode)
    {
        if (!lease.TryMarkReleased())
        {
            return;
        }

        if (!_entries.TryGetValue(lease.Instance.Key, out var entry))
        {
            lease.Instance.RemoveLease(lease);
            return;
        }

        var keyFingerprint = CreateTelemetryKeyFingerprint(lease.Instance.Key);
        LogReleaseRequested(_logger, keyFingerprint, releaseMode.ToString(), null);

        await entry.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            lease.Instance.RemoveLease(lease);
            if (!ReferenceEquals(entry.Instance, lease.Instance))
            {
                return;
            }

            if (entry.RefCount > 0)
            {
                var previousRefCount = entry.RefCount;
                entry.RefCount--;
                LogRefCountChanged(_logger, keyFingerprint, previousRefCount, entry.RefCount, "release", null);
            }

            if (entry.RefCount == 0)
            {
                if (releaseMode == InstanceLeaseReleaseMode.Immediate)
                {
                    await StopIdleInstanceAsync(entry, KillReasonImmediateRelease).ConfigureAwait(false);
                }
                else
                {
                    ScheduleIdleShutdown(entry);
                }
            }
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private async Task HandleCrashAsync(PooledOpenCodeInstance crashedInstance, Exception exception)
    {
        var keyFingerprint = CreateTelemetryKeyFingerprint(crashedInstance.Key);
        LogCrash(_logger, keyFingerprint, null);

        if (!_entries.TryGetValue(crashedInstance.Key, out var entry))
        {
            return;
        }

        await entry.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(entry.Instance, crashedInstance))
            {
                return;
            }

            entry.CancelIdleShutdown();
            LogProcessKill(_logger, keyFingerprint, KillReasonCrashRecovery, null);
            await crashedInstance.DisposeAsync().ConfigureAwait(false);
            entry.Instance = null;

            if (entry.RefCount == 0)
            {
                _entries.TryRemove(entry.Key, out _);
                return;
            }

            if (!entry.TryRecordRestart(DateTimeOffset.UtcNow))
            {
                LogRestartLimitExceeded(_logger, keyFingerprint, null);
                foreach (var lease in crashedInstance.Leases)
                {
                    if (lease.IsReleased)
                    {
                        continue;
                    }

                    lease.NotifyPermanentFault(exception);
                }

                _entries.TryRemove(entry.Key, out _);
                return;
            }

            var startupDirectory = entry.StartupDirectory ?? Environment.CurrentDirectory;
            var restartEnvironment = entry.RestartEnvironment ?? new Dictionary<string, string>();
            ProcessRestarts.Add(1);
            var replacementInstance = await SpawnAsync(
                    entry.Key,
                    startupDirectory,
                    restartEnvironment,
                    SpawnReasonCrashRestart,
                    CancellationToken.None)
                .ConfigureAwait(false);
            entry.Instance = replacementInstance;

            foreach (var lease in crashedInstance.Leases)
            {
                if (lease.IsReleased)
                {
                    continue;
                }

                var replacementLease = replacementInstance.CreateLease(ReleaseLeaseAsync);
                lease.NotifyReplaced(replacementLease);
            }
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private void ScheduleIdleShutdown(RegistryEntry entry)
    {
        entry.CancelIdleShutdown();
        var cts = new CancellationTokenSource();
        entry.IdleShutdown = cts;
        LogIdleTtlScheduled(_logger, CreateTelemetryKeyFingerprint(entry.Key), _idleTtl.TotalMilliseconds, null);

        _ = ShutdownWhenIdleAsync(entry, cts);
    }

    private async Task StopIdleInstanceAsync(RegistryEntry entry, string reason)
    {
        entry.CancelIdleShutdown();
        var keyFingerprint = CreateTelemetryKeyFingerprint(entry.Key);
        LogIdleShutdown(_logger, keyFingerprint, null);
        if (entry.Instance is not null)
        {
            LogProcessKill(_logger, keyFingerprint, reason, null);
            await entry.Instance.DisposeAsync().ConfigureAwait(false);
            entry.Instance = null;
            entry.RestartEnvironment = null;
        }

        _entries.TryRemove(entry.Key, out _);
    }

    private async Task ShutdownWhenIdleAsync(RegistryEntry entry, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(_idleTtl, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return;
        }

        await entry.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(entry.IdleShutdown, cts) || entry.RefCount != 0)
            {
                return;
            }

            LogIdleTtlExpired(_logger, CreateTelemetryKeyFingerprint(entry.Key), null);
            await StopIdleInstanceAsync(entry, KillReasonIdleTtl).ConfigureAwait(false);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private async Task<PooledOpenCodeInstance> SpawnAsync(
        string key,
        string directory,
        IReadOnlyDictionary<string, string> authoritativeEnvironment,
        string reason,
        CancellationToken ct)
    {
        var keyFingerprint = CreateTelemetryKeyFingerprint(key);
        LogSpawn(_logger, keyFingerprint, null);
        LogProcessSpawn(_logger, keyFingerprint, reason, null);
        var instance = await _instanceFactory(key, directory, authoritativeEnvironment, ct).ConfigureAwait(false);
        if (!string.Equals(instance.Key, key, StringComparison.Ordinal))
        {
            await instance.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException("Pooled OpenCode instance factory returned an instance for the wrong key.");
        }

        instance.Crashed += HandleCrashAsync;
        return instance;
    }

    private static IEnumerable<Measurement<int>> ObserveActiveInstances()
    {
        yield return new Measurement<int>(MetricRegistries.Values.Sum(registry => registry.GetActiveInstanceCount()));
    }

    private static IEnumerable<Measurement<int>> ObserveSessionsPerInstance()
    {
        foreach (var registry in MetricRegistries.Values)
        {
            foreach (var measurement in registry.GetSessionsPerInstanceMeasurements())
            {
                yield return measurement;
            }
        }
    }

    private static IEnumerable<Measurement<int>> ObservePoolUtilization()
    {
        foreach (var registry in MetricRegistries.Values)
        {
            foreach (var measurement in registry.GetSessionsPerInstanceMeasurements())
            {
                yield return measurement;
            }
        }
    }

    private int GetActiveInstanceCount()
    {
        return _entries.Values.Count(entry => entry.Instance?.IsAvailable == true);
    }

    private IEnumerable<Measurement<int>> GetSessionsPerInstanceMeasurements()
    {
        foreach (var entry in _entries.Values)
        {
            if (entry.Instance?.IsAvailable != true)
            {
                continue;
            }

            yield return new Measurement<int>(
                entry.RefCount,
                new KeyValuePair<string, object?>("pool.key_fingerprint", CreateTelemetryKeyFingerprint(entry.Key)));
        }
    }

    private static string CreateTelemetryKeyFingerprint(string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    private bool HasAvailableInstanceForDifferentCredentialBoundary(string credentialHashKey)
    {
        foreach (var entry in _entries.Values)
        {
            if (string.Equals(entry.Key, credentialHashKey, StringComparison.Ordinal))
            {
                continue;
            }

            if (entry.Instance?.IsAvailable == true)
            {
                return true;
            }
        }

        return false;
    }

    private sealed class RegistryEntry : IDisposable
    {
        public RegistryEntry(string key)
        {
            Key = key;
        }

        public string Key { get; }

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public PooledOpenCodeInstance? Instance { get; set; }

        public string? StartupDirectory { get; set; }

        public IReadOnlyDictionary<string, string>? RestartEnvironment { get; set; }

        public int RefCount { get; set; }

        public CancellationTokenSource? IdleShutdown { get; set; }

        private Queue<DateTimeOffset> RestartAttempts { get; } = new();

        public void CancelIdleShutdown()
        {
            var cts = IdleShutdown;
            IdleShutdown = null;
            if (cts is null)
            {
                return;
            }

            cts.Cancel();
            cts.Dispose();
        }

        public bool TryRecordRestart(DateTimeOffset now)
        {
            while (RestartAttempts.Count > 0 && now - RestartAttempts.Peek() > RestartWindow)
            {
                RestartAttempts.Dequeue();
            }

            if (RestartAttempts.Count >= MaxRestartAttempts)
            {
                return false;
            }

            RestartAttempts.Enqueue(now);
            return true;
        }

        public void Dispose()
        {
            CancelIdleShutdown();
            Gate.Dispose();
        }
    }
}

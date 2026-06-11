using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode.Pooling;

/// <summary>
/// Shared OpenCode process state for all leases in one composite pool partition
/// (owner identity + credential environment hash).
/// </summary>
internal sealed class PooledOpenCodeInstance : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, InstanceLease> _leases = new();
    private readonly Func<ValueTask> _shutdownAsync;
    private readonly OpenCodeProcessManager? _processManager;
    private readonly ILogger<PooledOpenCodeInstance>? _logger;
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _eventSubscriptionReady = new(StringComparer.Ordinal);
    private int _disposed;
    private int _faulted;

    public PooledOpenCodeInstance(
        string key,
        string instanceId,
        int? processId,
        Func<ValueTask> shutdownAsync)
        : this(key, instanceId, processId, null, null, shutdownAsync)
    {
    }

    public PooledOpenCodeInstance(
        string key,
        string instanceId,
        int? processId,
        Func<ValueTask> shutdownAsync,
        ILogger<PooledOpenCodeInstance> logger)
        : this(key, instanceId, processId, null, null, shutdownAsync, logger)
    {
    }

    public PooledOpenCodeInstance(
        string key,
        string instanceId,
        int? processId,
        OpenCodeHttpClient? httpClient,
        OpenCodeProcessManager? processManager,
        Func<ValueTask> shutdownAsync)
        : this(key, instanceId, processId, httpClient, processManager, shutdownAsync, null)
    {
    }

    public PooledOpenCodeInstance(
        string key,
        string instanceId,
        int? processId,
        OpenCodeHttpClient? httpClient,
        OpenCodeProcessManager? processManager,
        Func<ValueTask> shutdownAsync,
        ILogger<PooledOpenCodeInstance>? logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        Key = key;
        InstanceId = instanceId;
        ProcessId = processId;
        HttpClient = httpClient;
        _processManager = processManager;
        _logger = logger;
        _shutdownAsync = shutdownAsync;

        if (_processManager is not null)
        {
            _processManager.ProcessExited += OnProcessExited;
        }
    }

    public event Func<PooledOpenCodeInstance, Exception, Task>? Crashed;

    public string Key { get; }

    public string InstanceId { get; }

    public int? ProcessId { get; }

    public OpenCodeHttpClient? HttpClient { get; }

    public bool IsFaulted => Volatile.Read(ref _faulted) != 0;

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public bool IsAvailable => !IsFaulted && !IsDisposed;

    internal ICollection<InstanceLease> Leases => _leases.Values;

    internal Task WaitForEventSubscriptionAsync(string openCodeSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(openCodeSessionId);
        return _eventSubscriptionReady.GetOrAdd(openCodeSessionId, static _ => new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously)).Task;
    }

    internal void NotifyEventSubscriptionReady(string openCodeSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(openCodeSessionId);
        _eventSubscriptionReady.GetOrAdd(openCodeSessionId, static _ => new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }

    internal InstanceLease CreateLease(Func<InstanceLease, InstanceLeaseReleaseMode, ValueTask> releaseAsync)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (IsFaulted)
        {
            throw new InvalidOperationException("Cannot lease a faulted pooled OpenCode instance.");
        }

        var lease = new InstanceLease(this, releaseAsync);
        if (!_leases.TryAdd(lease.Id, lease))
        {
            throw new InvalidOperationException("Failed to register pooled OpenCode instance lease.");
        }

        return lease;
    }

    internal void RemoveLease(InstanceLease lease) => _leases.TryRemove(lease.Id, out _);

    internal async Task ReportCrashAsync(Exception exception)
    {
        if (Interlocked.Exchange(ref _faulted, 1) != 0)
        {
            return;
        }

        if (_logger is not null)
        {
            LogCrashDetected(_logger, ProcessId ?? 0, ProcessId.HasValue, null);
        }

        foreach (var lease in _leases.Values)
        {
            lease.NotifyFaulted(exception);
        }

        var crashed = Crashed;
        if (crashed is null)
        {
            return;
        }

        var handlers = crashed.GetInvocationList()
            .Cast<Func<PooledOpenCodeInstance, Exception, Task>>()
            .Select(handler => handler(this, exception));

        await Task.WhenAll(handlers).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_processManager is not null)
        {
            _processManager.ProcessExited -= OnProcessExited;
        }

        await _shutdownAsync().ConfigureAwait(false);
    }

    private void OnProcessExited(object? sender, int exitCode)
    {
        if (IsDisposed)
        {
            return;
        }

        if (_logger is not null)
        {
            LogProcessExited(_logger, exitCode, ProcessId ?? 0, ProcessId.HasValue, null);
        }

        var exception = new InvalidOperationException(
            $"Pooled OpenCode process exited with code {exitCode}.");

        _ = ReportCrashAsync(exception);
    }

    private static readonly Action<ILogger, int, bool, Exception?> LogCrashDetected =
        LoggerMessage.Define<int, bool>(LogLevel.Warning, new EventId(1, "CrashDetected"),
            "Pooled OpenCode process crash detected; process_id: {ProcessId}; has_process_id: {HasProcessId}.");

    private static readonly Action<ILogger, int, int, bool, Exception?> LogProcessExited =
        LoggerMessage.Define<int, int, bool>(LogLevel.Warning, new EventId(2, "ProcessExited"),
            "Pooled OpenCode process exited; exit_code: {ExitCode}; process_id: {ProcessId}; has_process_id: {HasProcessId}.");
}

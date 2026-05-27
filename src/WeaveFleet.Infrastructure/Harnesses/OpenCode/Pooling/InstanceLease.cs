namespace WeaveFleet.Infrastructure.Harnesses.OpenCode.Pooling;

/// <summary>
/// Represents one active reference to a pooled OpenCode process instance.
/// Disposing the lease releases the registry reference count exactly once.
/// </summary>
internal sealed class InstanceLease : IAsyncDisposable
{
    private readonly Func<InstanceLease, InstanceLeaseReleaseMode, ValueTask> _releaseAsync;
    private readonly TaskCompletionSource<Exception> _faulted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<InstanceLease> _replacement =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _released;

    internal InstanceLease(PooledOpenCodeInstance instance, Func<InstanceLease, InstanceLeaseReleaseMode, ValueTask> releaseAsync)
    {
        Id = Guid.NewGuid();
        Instance = instance;
        _releaseAsync = releaseAsync;
    }

    public Guid Id { get; }

    public PooledOpenCodeInstance Instance { get; }

    public Task<Exception> Faulted => _faulted.Task;

    public Task<InstanceLease> Replacement => _replacement.Task;

    public bool IsReleased => Volatile.Read(ref _released) != 0;

    public ValueTask DisposeAsync() => _releaseAsync(this, InstanceLeaseReleaseMode.IdleTtl);

    public ValueTask StopAsync() => _releaseAsync(this, InstanceLeaseReleaseMode.Immediate);

    internal bool TryMarkReleased() => Interlocked.Exchange(ref _released, 1) == 0;

    internal void NotifyFaulted(Exception exception) => _faulted.TrySetResult(exception);

    internal void NotifyReplaced(InstanceLease replacement) => _replacement.TrySetResult(replacement);

    internal void NotifyPermanentFault(Exception exception) => _replacement.TrySetException(exception);
}

internal enum InstanceLeaseReleaseMode
{
    IdleTtl,
    Immediate,
}

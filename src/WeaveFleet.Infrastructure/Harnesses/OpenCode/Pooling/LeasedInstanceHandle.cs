using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode.Pooling;

/// <summary>
/// Delegates harness session operations to a leased pooled OpenCode instance.
/// </summary>
internal sealed class LeasedInstanceHandle : IOpenCodeInstanceHandle
{
    private readonly Func<string?, string?, CancellationToken, Task<(InstanceLease Lease, long LeaseGeneration)>>? _acquireLeaseAsync;
    private readonly Func<string, PooledOpenCodeInstance, long, Task>? _sessionBoundAsync;
    private readonly SseEventDemultiplexer _demultiplexer;
    private readonly PoolDemuxBindingTable _bindingTable;
    private readonly string _workingDirectory;
    private readonly string _fleetSessionId;
    private readonly string _ownerUserId;
    private readonly Guid _consumerId;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _acquireLock = new(1, 1);
    private readonly CancellationTokenSource _faultMonitorCancellation = new();
    private InstanceLease? _lease;
    private OpenCodeHttpClient? _httpClient;
    private IAsyncDisposable? _eventSubscriptionRegistration;
    private Channel<OpenCodeSseEvent>? _eventSubscriptionChannel;
    private long _leaseGeneration;
    private int _disposed;
    private int _disconnected;

    public LeasedInstanceHandle(
        InstanceLease lease,
        SseEventDemultiplexer demultiplexer,
        PoolDemuxBindingTable bindingTable,
        string workingDirectory,
        string fleetSessionId,
        string ownerUserId,
        Guid consumerId,
        long leaseGeneration)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(demultiplexer);
        ArgumentNullException.ThrowIfNull(bindingTable);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(fleetSessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerUserId);
        if (leaseGeneration < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseGeneration), leaseGeneration, "Lease generation must be non-negative.");
        }

        _demultiplexer = demultiplexer;
        _bindingTable = bindingTable;
        _workingDirectory = workingDirectory;
        _fleetSessionId = fleetSessionId;
        _ownerUserId = ownerUserId;
        _consumerId = consumerId;
        _leaseGeneration = leaseGeneration;
        _lease = lease;

        _httpClient = (lease.Instance.HttpClient
            ?? throw new InvalidOperationException("Pooled OpenCode instance does not expose an HTTP client."))
            .WithExpectedDirectory(_workingDirectory);

        _ = MonitorFaultsAsync();
    }

    public LeasedInstanceHandle(
        Func<CancellationToken, Task<(InstanceLease Lease, long LeaseGeneration)>> acquireLeaseAsync,
        Func<string, PooledOpenCodeInstance, long, Task> sessionBoundAsync,
        SseEventDemultiplexer demultiplexer,
        PoolDemuxBindingTable bindingTable,
        string workingDirectory,
        string fleetSessionId,
        string ownerUserId,
        Guid consumerId)
        : this(
            (_, _, ct) => acquireLeaseAsync(ct),
            sessionBoundAsync,
            demultiplexer,
            bindingTable,
            workingDirectory,
            fleetSessionId,
            ownerUserId,
            consumerId)
    {
        ArgumentNullException.ThrowIfNull(acquireLeaseAsync);
    }

    public LeasedInstanceHandle(
        Func<string?, string?, CancellationToken, Task<(InstanceLease Lease, long LeaseGeneration)>> acquireLeaseAsync,
        Func<string, PooledOpenCodeInstance, long, Task> sessionBoundAsync,
        SseEventDemultiplexer demultiplexer,
        PoolDemuxBindingTable bindingTable,
        string workingDirectory,
        string fleetSessionId,
        string ownerUserId,
        Guid consumerId)
    {
        ArgumentNullException.ThrowIfNull(acquireLeaseAsync);
        ArgumentNullException.ThrowIfNull(sessionBoundAsync);
        ArgumentNullException.ThrowIfNull(demultiplexer);
        ArgumentNullException.ThrowIfNull(bindingTable);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(fleetSessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerUserId);

        _acquireLeaseAsync = acquireLeaseAsync;
        _sessionBoundAsync = sessionBoundAsync;
        _demultiplexer = demultiplexer;
        _bindingTable = bindingTable;
        _workingDirectory = workingDirectory;
        _fleetSessionId = fleetSessionId;
        _ownerUserId = ownerUserId;
        _consumerId = consumerId;
    }

    public event EventHandler<int>? ProcessExited;

    private event Action? EventSubscriptionChannelSwapped;

    public OpenCodeHttpClient HttpClient
    {
        get
        {
            lock (_sync)
            {
                return _httpClient
                    ?? throw new InvalidOperationException("Pooled OpenCode instance has not been acquired yet.");
            }
        }
    }

    public int? ProcessId => TryGetCurrentLease()?.Instance.ProcessId;

    public bool IsAcquired => TryGetCurrentLease() is not null;

    public bool IsRunning => Volatile.Read(ref _disconnected) == 0 && TryGetCurrentLease()?.Instance.IsAvailable == true;

    public async Task EnsureConnectedAsync(CancellationToken ct)
    {
        await EnsureConnectedAsync(providerId: null, modelId: null, ct).ConfigureAwait(false);
    }

    public async Task EnsureConnectedAsync(string? providerId, string? modelId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await EnsureAcquiredAsync(providerId, modelId, ct).ConfigureAwait(false);
        var currentLease = CurrentLease;
        if (!currentLease.Instance.IsAvailable && Interlocked.Exchange(ref _disconnected, 1) == 0)
        {
            ProcessExited?.Invoke(this, -1);
        }

        if (Volatile.Read(ref _disconnected) == 0)
        {
            return;
        }

        await ReconnectIfDisconnectedAsync(ct).ConfigureAwait(false);
    }

    public async Task WaitForEventSubscriptionAsync(string openCodeSessionId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(openCodeSessionId);
        await EnsureAcquiredAsync(ct).ConfigureAwait(false);
        var lease = CurrentLease;
        await lease.Instance.WaitForEventSubscriptionAsync(openCodeSessionId).WaitAsync(ct).ConfigureAwait(false);
    }

    public async Task SendCommandAsync(string openCodeSessionId, OpenCodeCommandRequest request, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(openCodeSessionId);
        ArgumentNullException.ThrowIfNull(request);
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        var lease = CurrentLease;
        var leaseGeneration = CurrentLeaseGeneration;

        if (!_bindingTable.TryVerifyCommandBinding(
                lease.Instance,
                _fleetSessionId,
                _ownerUserId,
                openCodeSessionId,
                _workingDirectory,
                leaseGeneration,
                out _))
        {
            throw new InvalidOperationException(
                "Cannot route OpenCode command because the pooled session binding does not match the Fleet session, user, OpenCode session, directory, and lease generation.");
        }

        await HttpClient.SendCommandAsync(openCodeSessionId, request, _workingDirectory, ct).ConfigureAwait(false);
    }

    public async Task<PoolDemuxBinding> BindSessionAsync(string openCodeSessionId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(openCodeSessionId);
        await EnsureConnectedAsync(ct).ConfigureAwait(false);

        var lease = CurrentLease;
        var leaseGeneration = CurrentLeaseGeneration;

        _bindingTable.Bind(
            lease.Instance,
            openCodeSessionId,
            _consumerId,
            _fleetSessionId,
            _ownerUserId,
            _workingDirectory,
            leaseGeneration);

        await EnsureEventSubscriptionRegisteredAsync(ct).ConfigureAwait(false);

        if (!_bindingTable.TryGetBinding(lease.Instance, _workingDirectory, openCodeSessionId, leaseGeneration, out var binding))
        {
            throw new InvalidOperationException("Cannot bind pooled OpenCode session to the active lease.");
        }

        await _demultiplexer.WaitForActiveStreamAsync(lease.Instance, _workingDirectory, ct).ConfigureAwait(false);

        if (_sessionBoundAsync is not null)
        {
            await _sessionBoundAsync(openCodeSessionId, lease.Instance, leaseGeneration).ConfigureAwait(false);
        }

        return binding;
    }

    public async IAsyncEnumerable<OpenCodeSseEvent> SubscribeEvents(
        string? openCodeSessionId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(openCodeSessionId);
        await EnsureConnectedAsync(ct).ConfigureAwait(false);

        var subscribedLease = CurrentLease;
        var subscribedLeaseGeneration = CurrentLeaseGeneration;
        var channel = CurrentEventSubscriptionChannel;

        subscribedLease.Instance.NotifyEventSubscriptionReady(openCodeSessionId);

        var faultTask = subscribedLease.Faulted;
        var channelSwapped = false;
        void HandleChannelSwapped()
        {
            channelSwapped = true;
        }

        EventSubscriptionChannelSwapped += HandleChannelSwapped;

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var readTask = channel.Reader.ReadAsync(ct).AsTask();
                var completedTask = await Task.WhenAny(readTask, faultTask).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (completedTask == faultTask)
                {
                    await faultTask.ConfigureAwait(false);
                    if (!channelSwapped)
                    {
                        if (TryGetCurrentLease()?.Replacement.IsCompleted == true)
                        {
                            await ReconnectIfDisconnectedAsync(ct).ConfigureAwait(false);
                        }
                        else
                        {
                            yield break;
                        }
                    }

                    channelSwapped = false;
                    subscribedLease = CurrentLease;
                    subscribedLeaseGeneration = CurrentLeaseGeneration;
                    channel = CurrentEventSubscriptionChannel;
                    faultTask = subscribedLease.Faulted;
                    subscribedLease.Instance.NotifyEventSubscriptionReady(openCodeSessionId);
                    continue;
                }

                yield return await readTask.ConfigureAwait(false);
            }
        }
        finally
        {
            EventSubscriptionChannelSwapped -= HandleChannelSwapped;
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await DisposeCoreAsync(InstanceLeaseReleaseMode.IdleTtl).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeCoreAsync(InstanceLeaseReleaseMode.IdleTtl).ConfigureAwait(false);
    }

    private async ValueTask DisposeCoreAsync(InstanceLeaseReleaseMode releaseMode)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _faultMonitorCancellation.CancelAsync().ConfigureAwait(false);
        _faultMonitorCancellation.Dispose();
        _acquireLock.Dispose();
        if (_eventSubscriptionRegistration is not null)
        {
            await _eventSubscriptionRegistration.DisposeAsync().ConfigureAwait(false);
        }

        if (TryGetCurrentLease() is null)
        {
            return;
        }

        _bindingTable.RemoveForLease(
            CurrentLease.Instance,
            _consumerId,
            _fleetSessionId,
            _workingDirectory,
            CurrentLeaseGeneration);
        if (releaseMode == InstanceLeaseReleaseMode.Immediate)
        {
            await ResolveLeaseForRelease().StopAsync().ConfigureAwait(false);
        }
        else
        {
            await ResolveLeaseForRelease().DisposeAsync().ConfigureAwait(false);
        }
    }

    private InstanceLease ResolveLeaseForRelease()
    {
        var lease = CurrentLease;
        if (Volatile.Read(ref _disconnected) == 0 || !lease.Replacement.IsCompletedSuccessfully)
        {
            return lease;
        }

        return lease.Replacement.Result;
    }

    private async Task EnsureAcquiredAsync(CancellationToken ct)
    {
        await EnsureAcquiredAsync(providerId: null, modelId: null, ct).ConfigureAwait(false);
    }

    private async Task EnsureAcquiredAsync(string? providerId, string? modelId, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (TryGetCurrentLease() is not null)
        {
            await EnsureEventSubscriptionRegisteredAsync(ct).ConfigureAwait(false);
            return;
        }

        if (_acquireLeaseAsync is null)
        {
            throw new InvalidOperationException("Pooled OpenCode instance lease is not available.");
        }

        await _acquireLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_lease is not null)
            {
                return;
            }

            var acquired = await _acquireLeaseAsync(providerId, modelId, ct).ConfigureAwait(false);
            ArgumentNullException.ThrowIfNull(acquired.Lease);
            if (acquired.LeaseGeneration < 0)
            {
                throw new InvalidOperationException("Lease generation must be non-negative.");
            }

            var httpClient = (acquired.Lease.Instance.HttpClient
                ?? throw new InvalidOperationException("Pooled OpenCode instance does not expose an HTTP client."))
                .WithExpectedDirectory(_workingDirectory);
            var subscription = await CreateEventSubscriptionAsync(acquired.Lease, acquired.LeaseGeneration, ct).ConfigureAwait(false);

            lock (_sync)
            {
                _lease = acquired.Lease;
                _leaseGeneration = acquired.LeaseGeneration;
                _httpClient = httpClient;
                _eventSubscriptionRegistration = subscription.Registration;
                _eventSubscriptionChannel = subscription.Channel;
                Volatile.Write(ref _disconnected, 0);
                EventSubscriptionChannelSwapped?.Invoke();
                _ = MonitorFaultsAsync();
            }
        }
        finally
        {
            _acquireLock.Release();
        }
    }

    private async Task EnsureEventSubscriptionRegisteredAsync(CancellationToken ct)
    {
        lock (_sync)
        {
            if (_eventSubscriptionRegistration is not null)
            {
                return;
            }
        }

        await _acquireLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (_eventSubscriptionRegistration is not null)
                {
                    return;
                }
            }

            var lease = CurrentLease;
            var leaseGeneration = CurrentLeaseGeneration;
            var subscription = await CreateEventSubscriptionAsync(lease, leaseGeneration, ct).ConfigureAwait(false);
            var disposeSubscription = false;
            lock (_sync)
            {
                if (_eventSubscriptionRegistration is not null)
                {
                    disposeSubscription = true;
                }
                else
                {
                    _eventSubscriptionRegistration = subscription.Registration;
                    _eventSubscriptionChannel = subscription.Channel;
                }
            }

            if (disposeSubscription)
            {
                await subscription.Registration.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _acquireLock.Release();
        }
    }

    private InstanceLease? TryGetCurrentLease()
    {
        lock (_sync)
        {
            return _lease;
        }
    }

    private InstanceLease CurrentLease
    {
        get
        {
            lock (_sync)
            {
                return _lease ?? throw new InvalidOperationException("Pooled OpenCode instance has not been acquired yet.");
            }
        }
    }

    private long CurrentLeaseGeneration
    {
        get
        {
            lock (_sync)
            {
                return _leaseGeneration;
            }
        }
    }

    private Channel<OpenCodeSseEvent> CurrentEventSubscriptionChannel
    {
        get
        {
            lock (_sync)
            {
                return _eventSubscriptionChannel
                    ?? throw new InvalidOperationException("Pooled OpenCode SSE subscription has not been established yet.");
            }
        }
    }

    private async Task ReconnectAsync(CancellationToken ct)
    {
        while (Volatile.Read(ref _disconnected) != 0)
        {
            var lease = CurrentLease;
            var replacement = await lease.Replacement.WaitAsync(ct).ConfigureAwait(false);
            var replacementLeaseGeneration = CurrentLeaseGeneration + 1;
            _bindingTable.MoveBindings(
                lease.Instance,
                replacement.Instance,
                _consumerId,
                _workingDirectory,
                CurrentLeaseGeneration,
                replacementLeaseGeneration);
            var httpClient = (replacement.Instance.HttpClient
                ?? throw new InvalidOperationException("Pooled OpenCode instance does not expose an HTTP client."))
                .WithExpectedDirectory(_workingDirectory);
            var previousRegistration = CurrentEventSubscriptionRegistration;
            if (previousRegistration is not null)
            {
                await previousRegistration.DisposeAsync().ConfigureAwait(false);
            }

            var subscription = await CreateEventSubscriptionAsync(replacement, replacementLeaseGeneration, ct).ConfigureAwait(false);
            var disposeSubscription = false;

            lock (_sync)
            {
                if (!ReferenceEquals(_lease, lease))
                {
                    disposeSubscription = true;
                }
                else
                {
                    _lease = replacement;
                    _httpClient = httpClient;
                    _leaseGeneration = replacementLeaseGeneration;
                    _eventSubscriptionRegistration = subscription.Registration;
                    _eventSubscriptionChannel = subscription.Channel;
                    Volatile.Write(ref _disconnected, 0);
                    EventSubscriptionChannelSwapped?.Invoke();
                    _ = MonitorFaultsAsync();
                }
            }

            if (disposeSubscription)
            {
                await subscription.Registration.DisposeAsync().ConfigureAwait(false);
                continue;
            }
        }
    }

    private async Task ReconnectIfDisconnectedAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _disconnected) == 0)
        {
            return;
        }

        await _acquireLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disconnected) != 0)
            {
                await ReconnectAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _acquireLock.Release();
        }
    }

    private IAsyncDisposable? CurrentEventSubscriptionRegistration
    {
        get
        {
            lock (_sync)
            {
                return _eventSubscriptionRegistration;
            }
        }
    }

    private async Task<EventSubscription> CreateEventSubscriptionAsync(InstanceLease lease, long leaseGeneration, CancellationToken ct)
    {
        var channel = Channel.CreateBounded<OpenCodeSseEvent>(new BoundedChannelOptions(1000)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

        var registration = await _demultiplexer.RegisterConsumerAsync(
            lease.Instance,
            _workingDirectory,
            _consumerId,
            leaseGeneration,
            channel,
            ct).ConfigureAwait(false);

        return new EventSubscription(registration, channel);
    }

    private readonly record struct EventSubscription(IAsyncDisposable Registration, Channel<OpenCodeSseEvent> Channel);

    private async Task MonitorFaultsAsync()
    {
        try
        {
            var lease = CurrentLease;
            await lease.Faulted.WaitAsync(_faultMonitorCancellation.Token).ConfigureAwait(false);
            if (Interlocked.Exchange(ref _disconnected, 1) == 0)
            {
                ProcessExited?.Invoke(this, -1);
            }
        }
        catch (OperationCanceledException) when (_faultMonitorCancellation.IsCancellationRequested)
        {
        }
    }
}

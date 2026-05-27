using WeaveFleet.Infrastructure.Harnesses.OpenCode.Pooling;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode;

/// <summary>
/// Owns the per-session OpenCode process used when pooled mode is disabled.
/// </summary>
internal sealed class OwnedInstanceHandle : IOpenCodeInstanceHandle
{
    private readonly OpenCodeProcessManager _processManager;
    private readonly PortAllocator _portAllocator;
    private readonly int _allocatedPort;
    private readonly string _workingDirectory;
    private readonly TimeSpan _shutdownTimeout;
    private int _portReleased;

    public OwnedInstanceHandle(
        OpenCodeHttpClient httpClient,
        OpenCodeProcessManager processManager,
        PortAllocator portAllocator,
        int allocatedPort,
        string workingDirectory,
        TimeSpan shutdownTimeout)
    {
        HttpClient = httpClient;
        _processManager = processManager;
        _portAllocator = portAllocator;
        _allocatedPort = allocatedPort;
        _workingDirectory = workingDirectory;
        _shutdownTimeout = shutdownTimeout;
    }

    public event EventHandler<int>? ProcessExited
    {
        add => _processManager.ProcessExited += value;
        remove => _processManager.ProcessExited -= value;
    }

    public OpenCodeHttpClient HttpClient { get; }

    public int? ProcessId => _processManager.ProcessId;

    public bool IsRunning => _processManager.IsRunning;

    public Task EnsureConnectedAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!_processManager.IsRunning)
        {
            throw new InvalidOperationException("OpenCode process is not running.");
        }

        return Task.CompletedTask;
    }

    public Task WaitForEventSubscriptionAsync(string openCodeSessionId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(openCodeSessionId);
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task SendCommandAsync(string openCodeSessionId, OpenCodeCommandRequest request, CancellationToken ct) =>
        HttpClient.SendCommandAsync(openCodeSessionId, request, _workingDirectory, ct);

    public IAsyncEnumerable<OpenCodeSseEvent> SubscribeEvents(string? openCodeSessionId, CancellationToken ct) =>
        HttpClient.SubscribeToEventsAsync(_workingDirectory, ct);

    public async Task StopAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await _processManager.StopAsync(_shutdownTimeout).ConfigureAwait(false);
        ReleasePort();
    }

    public async ValueTask DisposeAsync()
    {
        await _processManager.DisposeAsync().ConfigureAwait(false);
        ReleasePort();
    }

    private void ReleasePort()
    {
        if (_allocatedPort > 0 && Interlocked.Exchange(ref _portReleased, 1) == 0)
        {
            _portAllocator.ReleasePort(_allocatedPort);
        }
    }
}

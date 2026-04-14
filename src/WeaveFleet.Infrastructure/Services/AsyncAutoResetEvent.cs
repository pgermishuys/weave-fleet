using System.Threading;

namespace WeaveFleet.Infrastructure.Services;

public sealed class AsyncAutoResetEvent : IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _semaphore = new(0, 1);

    public Task WaitAsync(CancellationToken cancellationToken)
        => _semaphore.WaitAsync(cancellationToken);

    public void Set()
    {
        lock (_gate)
        {
            if (_semaphore.CurrentCount == 0)
                _semaphore.Release();
        }
    }

    public void Dispose()
    {
        _semaphore.Dispose();
    }
}

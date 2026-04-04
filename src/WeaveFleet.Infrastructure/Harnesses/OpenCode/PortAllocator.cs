using Microsoft.Extensions.Logging;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode;

/// <summary>
/// Thread-safe port allocator that hands out unique ports from a configured range.
/// </summary>
public sealed class PortAllocator
{
    private static readonly Action<ILogger, int, int, int, Exception?> LogPortNotAllocated =
        LoggerMessage.Define<int, int, int>(
            LogLevel.Warning,
            new EventId(1, nameof(ReleasePort)),
            "Attempted to release port {Port} that was not allocated from range {Start}-{End}.");

    private readonly int _rangeStart;
    private readonly int _rangeEnd;
    private readonly HashSet<int> _allocated;
    private readonly ILogger<PortAllocator>? _logger;
    private int _next;
    private readonly object _lock = new();

    /// <summary>Initialises the allocator with the inclusive port range [<paramref name="rangeStart"/>, <paramref name="rangeEnd"/>].</summary>
    public PortAllocator(int rangeStart, int rangeEnd, ILogger<PortAllocator>? logger = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rangeStart, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rangeEnd, rangeStart);

        _rangeStart = rangeStart;
        _rangeEnd = rangeEnd;
        _next = rangeStart;
        _allocated = new HashSet<int>(rangeEnd - rangeStart + 1);
        _logger = logger;
    }

    /// <summary>Total number of ports currently allocated.</summary>
    public int AllocatedCount
    {
        get { lock (_lock) { return _allocated.Count; } }
    }

    /// <summary>Total number of ports still available for allocation.</summary>
    public int AvailableCount
    {
        get { lock (_lock) { return (_rangeEnd - _rangeStart + 1) - _allocated.Count; } }
    }

    /// <summary>
    /// Returns the next available port from the configured range.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when all ports are allocated.</exception>
    public int AllocatePort()
    {
        lock (_lock)
        {
            int count = _rangeEnd - _rangeStart + 1;

            // Try from _next, wrapping around once if needed.
            for (int attempts = 0; attempts < count; attempts++)
            {
                int candidate = _rangeStart + ((_next - _rangeStart + attempts) % count);
                if (_allocated.Add(candidate))
                {
                    _next = _rangeStart + ((candidate - _rangeStart + 1) % count);
                    return candidate;
                }
            }

            throw new InvalidOperationException(
                $"Port range exhausted: all {count} ports in {_rangeStart}-{_rangeEnd} are allocated.");
        }
    }

    /// <summary>Returns <paramref name="port"/> to the available pool.</summary>
    public void ReleasePort(int port)
    {
        lock (_lock)
        {
            if (!_allocated.Remove(port))
            {
                if (_logger is not null)
                {
                    LogPortNotAllocated(_logger, port, _rangeStart, _rangeEnd, null);
                }
            }
        }
    }
}

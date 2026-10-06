using System.Collections.Concurrent;
using WeaveFleet.Application.Devices;

namespace WeaveFleet.Api.Auth;

/// <summary>
/// The long-lived connections a paired device has open: hub connections and terminal sockets. Removing the device
/// closes them, so a phone that's been removed stops seeing events at once instead of whenever it next reconnects.
/// Ordinary requests need nothing: every one re-checks the token or cookie.
/// </summary>
public sealed class DeviceConnections : IDisposable
{
    private readonly DeviceTokenService _devices;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<long, Action>> _byDevice = new(StringComparer.Ordinal);

    // Devices removed while this process runs. A connection that passed sign-in just before the removal is closed as
    // soon as it's tracked. Device ids are never reused.
    private readonly ConcurrentDictionary<string, byte> _removed = new(StringComparer.Ordinal);
    private long _nextId;

    public DeviceConnections(DeviceTokenService devices)
    {
        _devices = devices;
        _devices.Revoked += Close;
    }

    /// <summary>Tracks a connection; <paramref name="close"/> runs if the device is removed. Dispose when it ends.</summary>
    public IDisposable Track(string deviceId, Action close)
    {
        var id = Interlocked.Increment(ref _nextId);
        var connections = _byDevice.GetOrAdd(deviceId, _ => new ConcurrentDictionary<long, Action>());
        connections[id] = close;
        if (_removed.ContainsKey(deviceId))
            Close(deviceId);
        return new Registration(this, deviceId, id);
    }

    /// <summary>How many connections the device has open (for tests).</summary>
    internal int CountFor(string deviceId) => _byDevice.TryGetValue(deviceId, out var connections) ? connections.Count : 0;

    private void Close(string deviceId)
    {
        _removed[deviceId] = 0;
        if (!_byDevice.TryGetValue(deviceId, out var connections))
            return;
        foreach (var (id, close) in connections)
        {
            if (!connections.TryRemove(id, out _))
                continue;
            try
            {
                close();
            }
            catch (ObjectDisposedException)
            {
                // Already closed on its own.
            }
        }
    }

    // The per-device map stays when empty: there's one per paired device, and dropping it could race a new Track.
    private void Untrack(string deviceId, long id)
    {
        if (_byDevice.TryGetValue(deviceId, out var connections))
            connections.TryRemove(id, out _);
    }

    public void Dispose() => _devices.Revoked -= Close;

    private sealed class Registration(DeviceConnections owner, string deviceId, long id) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.Untrack(deviceId, id);
        }
    }
}

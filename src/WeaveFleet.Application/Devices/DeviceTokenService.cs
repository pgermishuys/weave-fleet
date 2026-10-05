using System.Collections.Concurrent;
using System.Security.Cryptography;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Devices;

/// <summary>
/// Issues, checks and removes device tokens (<see cref="DeviceToken"/>). A token stops working when its device is
/// removed or when it hasn't been used for <see cref="Expiry"/>; every use moves that point forward.
/// <para>
/// The auth handler calls <see cref="ValidateAsync"/> on every request a device makes, so devices are cached for
/// <see cref="CacheFor"/> and the last-used time is written at most every <see cref="TouchEvery"/>. Removing a device
/// drops it from the cache at once.
/// </para>
/// </summary>
public sealed class DeviceTokenService(IDeviceRepository devices, TimeProvider time)
{
    public static readonly TimeSpan Expiry = TimeSpan.FromDays(30);
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan TouchEvery = TimeSpan.FromMinutes(5);

    // Compared against when the id is unknown, so a miss costs the same hash comparison as a hit.
    private static readonly byte[] DummyHash = SHA256.HashData("no such device"u8);

    private readonly ConcurrentDictionary<string, CachedDevice> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastTouched = new(StringComparer.Ordinal);

    // Removed here: a load or touch that was already under way must not put the device back. Ids are never reused.
    private readonly ConcurrentDictionary<string, byte> _removed = new(StringComparer.Ordinal);

    /// <summary>Raised after a device is removed, with its id.</summary>
    public event Action<string>? Revoked;

    /// <summary>Makes a device and its token. The token is returned once and never stored.</summary>
    public async Task<(DeviceSummary Device, string Token)> IssueAsync(string name, string? platform, string? pairedVia = null)
    {
        var id = Ulid.NewUlid().ToString();
        var (token, hash) = DeviceToken.Create(id);
        var now = time.GetUtcNow();
        var device = new Device
        {
            Id = id,
            Name = name,
            Platform = platform,
            TokenHash = hash,
            PairedVia = pairedVia,
            CreatedAt = now,
            LastUsedAt = now,
        };

        await devices.InsertAsync(device);
        _cache.TryRemove(id, out _);
        _lastTouched[id] = now;
        return (DeviceSummary.From(device), token);
    }

    /// <summary>The device a token belongs to, or null when the token is malformed, wrong, removed or expired.</summary>
    public async Task<DeviceValidation?> ValidateAsync(string presented)
    {
        if (!DeviceToken.TryParse(presented, out var deviceId, out var secret))
            return null;

        var device = await LoadAsync(deviceId);
        var matches = CryptographicOperations.FixedTimeEquals(DeviceToken.Hash(secret), device?.TokenHash ?? DummyHash);
        if (!matches || device is null || !IsLive(device))
            return null;

        await TouchAsync(device);
        return new DeviceValidation(device.Id, device.Name);
    }

    /// <summary>
    /// The device with this id when it still has access. A device's sign-in cookie carries only its id, so the cookie
    /// is checked with this instead of the token.
    /// </summary>
    public async Task<DeviceValidation?> ValidateDeviceAsync(string deviceId)
    {
        var device = await LoadAsync(deviceId);
        if (device is null || !IsLive(device))
            return null;

        await TouchAsync(device);
        return new DeviceValidation(device.Id, device.Name);
    }

    /// <summary>Devices that still have access, oldest first. Expired ones are left out.</summary>
    public async Task<IReadOnlyList<DeviceSummary>> ListAsync()
    {
        var active = await devices.ListActiveAsync();
        return active.Where(IsLive).Select(DeviceSummary.From).ToList();
    }

    /// <summary>Removes a device's access. False when there's no such device or it was already removed.</summary>
    public async Task<bool> RevokeAsync(string deviceId)
    {
        _removed[deviceId] = 0;
        var revoked = await devices.RevokeAsync(deviceId, time.GetUtcNow());
        _cache.TryRemove(deviceId, out _);
        _lastTouched.TryRemove(deviceId, out _);
        if (revoked)
            Revoked?.Invoke(deviceId);
        return revoked;
    }

    private bool IsLive(Device device) =>
        device.RevokedAt is null && !_removed.ContainsKey(device.Id) && device.LastUsedAt + Expiry >= time.GetUtcNow();

    private async Task<Device?> LoadAsync(string deviceId)
    {
        var now = time.GetUtcNow();
        if (_cache.TryGetValue(deviceId, out var cached) && now - cached.LoadedAt < CacheFor)
            return cached.Device;

        // Only devices that exist are cached: ids from requests are anyone's to choose, and caching misses would let a
        // stream of made-up tokens grow the cache without end.
        var device = await devices.GetAsync(deviceId);
        if (device is not null && !_removed.ContainsKey(deviceId))
            _cache[deviceId] = new CachedDevice(device, now);
        return device;
    }

    private async Task TouchAsync(Device device)
    {
        var now = time.GetUtcNow();
        var last = _lastTouched.GetOrAdd(device.Id, device.LastUsedAt);
        if (now - last < TouchEvery)
            return;

        // Only one request per interval writes: the one that swaps the time in.
        if (!_lastTouched.TryUpdate(device.Id, now, last))
            return;

        await devices.TouchAsync(device.Id, now);
        if (!_removed.ContainsKey(device.Id) && _cache.TryGetValue(device.Id, out var cached) && cached.Device is not null)
            _cache[device.Id] = cached with { Device = cached.Device with { LastUsedAt = now } };
    }

    private sealed record CachedDevice(Device Device, DateTimeOffset LoadedAt);
}

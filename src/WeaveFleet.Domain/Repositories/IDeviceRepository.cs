using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Domain.Repositories;

/// <summary>
/// Devices with their own access token (<see cref="Device"/>). Devices belong to the machine, not to a user: only
/// local mode, where whoever holds the machine token owns the machine, pairs them.
/// </summary>
public interface IDeviceRepository
{
    /// <summary>The device with this id, removed or not; null when there is none.</summary>
    Task<Device?> GetAsync(string id);

    /// <summary>Devices that haven't been removed, oldest first.</summary>
    Task<IReadOnlyList<Device>> ListActiveAsync();

    Task InsertAsync(Device device);

    /// <summary>Records that the device used its token at <paramref name="lastUsedAt"/>.</summary>
    Task TouchAsync(string id, DateTimeOffset lastUsedAt);

    /// <summary>
    /// Gives a device that hasn't been removed a new token hash (its old token stops working) and records it as used at
    /// <paramref name="at"/>. False when there's no such device or it was removed.
    /// </summary>
    Task<bool> ReplaceTokenHashAsync(string id, byte[] tokenHash, DateTimeOffset at);

    /// <summary>Removes the device's access. False when there's no such device or it was already removed.</summary>
    Task<bool> RevokeAsync(string id, DateTimeOffset at);
}

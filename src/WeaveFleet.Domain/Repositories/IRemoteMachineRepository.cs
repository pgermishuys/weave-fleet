using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Domain.Repositories;

/// <summary>The machines this Fleet knows (<see cref="RemoteMachine"/>) and the device grants made on them.</summary>
public interface IRemoteMachineRepository
{
    Task<IReadOnlyList<RemoteMachine>> ListAsync();

    Task<RemoteMachine?> GetAsync(string id);

    /// <summary>Adds the machine, or replaces the one with the same id (keeping when it was added).</summary>
    Task UpsertAsync(RemoteMachine machine);

    /// <summary>Deletes the machine and every grant on it.</summary>
    Task<bool> DeleteAsync(string id);

    Task UpdateStatusAsync(string id, string status, DateTimeOffset? lastSeenAt);

    Task<DeviceGrant?> GetGrantAsync(string deviceId, string machineId);

    Task SaveGrantAsync(DeviceGrant grant);

    Task<IReadOnlyList<DeviceGrant>> ListGrantsForDeviceAsync(string deviceId);

    /// <summary>Grants whose device was removed here but not yet on the other machine.</summary>
    Task<IReadOnlyList<DeviceGrant>> ListRevokedGrantsAsync();

    Task MarkGrantRevokedAsync(string deviceId, string machineId, DateTimeOffset at);

    Task DeleteGrantAsync(string deviceId, string machineId);
}

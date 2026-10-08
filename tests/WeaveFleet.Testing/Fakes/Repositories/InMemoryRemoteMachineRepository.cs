using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Testing.Fakes.Repositories;

public sealed class InMemoryRemoteMachineRepository : IRemoteMachineRepository
{
    private readonly Dictionary<string, RemoteMachine> _machines = new(StringComparer.Ordinal);
    private readonly List<DeviceGrant> _grants = [];
    private readonly Lock _gate = new();

    public Task<IReadOnlyList<RemoteMachine>> ListAsync()
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<RemoteMachine>>([.. _machines.Values]);
    }

    public Task<RemoteMachine?> GetAsync(string id)
    {
        lock (_gate)
            return Task.FromResult(_machines.GetValueOrDefault(id));
    }

    public Task UpsertAsync(RemoteMachine machine)
    {
        lock (_gate)
            _machines[machine.Id] = _machines.TryGetValue(machine.Id, out var existing) ? machine with { AddedAt = existing.AddedAt } : machine;
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(string id)
    {
        lock (_gate)
        {
            _grants.RemoveAll(grant => grant.MachineId == id);
            return Task.FromResult(_machines.Remove(id));
        }
    }

    public Task UpdateStatusAsync(string id, string status, DateTimeOffset? lastSeenAt)
    {
        lock (_gate)
        {
            if (_machines.TryGetValue(id, out var machine))
                _machines[id] = machine with { Status = status, LastSeenAt = lastSeenAt ?? machine.LastSeenAt };
        }

        return Task.CompletedTask;
    }

    public Task<DeviceGrant?> GetGrantAsync(string deviceId, string machineId)
    {
        lock (_gate)
            return Task.FromResult(_grants.FirstOrDefault(grant => grant.DeviceId == deviceId && grant.MachineId == machineId));
    }

    public Task SaveGrantAsync(DeviceGrant grant)
    {
        lock (_gate)
        {
            _grants.RemoveAll(existing => existing.DeviceId == grant.DeviceId && existing.MachineId == grant.MachineId);
            _grants.Add(grant);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DeviceGrant>> ListGrantsForDeviceAsync(string deviceId)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<DeviceGrant>>(_grants.Where(grant => grant.DeviceId == deviceId && grant.RevokedAt is null).ToList());
    }

    public Task<IReadOnlyList<DeviceGrant>> ListGrantsForMachineAsync(string machineId)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<DeviceGrant>>(_grants.Where(grant => grant.MachineId == machineId).ToList());
    }

    public Task<IReadOnlyList<DeviceGrant>> ListRevokedGrantsAsync()
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<DeviceGrant>>(_grants.Where(grant => grant.RevokedAt is not null).ToList());
    }

    public Task MarkGrantRevokedAsync(string deviceId, string machineId, DateTimeOffset at)
    {
        lock (_gate)
        {
            var index = _grants.FindIndex(grant => grant.DeviceId == deviceId && grant.MachineId == machineId);
            if (index >= 0)
                _grants[index] = _grants[index] with { RevokedAt = at };
        }

        return Task.CompletedTask;
    }

    public Task DeleteGrantAsync(string deviceId, string machineId)
    {
        lock (_gate)
            _grants.RemoveAll(grant => grant.DeviceId == deviceId && grant.MachineId == machineId);
        return Task.CompletedTask;
    }
}

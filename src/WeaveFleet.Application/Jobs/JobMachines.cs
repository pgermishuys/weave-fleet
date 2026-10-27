using System.Collections.Concurrent;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Machines;
using WeaveFleet.Application.Workspaces;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Jobs;

/// <summary>The machines jobs can go to, as the matcher sees them.</summary>
public interface IJobMachines
{
    /// <summary>This machine and every machine in the list, each with its seats and labels, and what it can run when it takes jobs.</summary>
    Task<IReadOnlyList<JobMachine>> ListAsync(CancellationToken ct);
}

/// <summary>
/// What the last check of the machines found, for reading why a job waits without asking every machine again, and the
/// origin remotes of repositories on other machines, which don't change often enough to ask for again each time.
/// </summary>
public sealed class JobMachineCache
{
    public IReadOnlyList<JobMachine>? Last { get; set; }

    public ConcurrentDictionary<string, string> RemoteUrls { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Reads the machines from what Fleet already knows: this machine's folders and harnesses, and each other machine
/// through its API with the token kept for it. Only a machine that takes jobs is asked.
/// </summary>
public sealed class JobMachines(
    IJobRepository jobs,
    MachineIdentityStore identity,
    RepositoryService repositories,
    MachineCapabilitiesReader capabilities,
    RemoteMachineService machines,
    RemoteSessions remote,
    JobMachineCache cache) : IJobMachines
{
    public async Task<IReadOnlyList<JobMachine>> ListAsync(CancellationToken ct)
    {
        var settings = (await jobs.ListMachineSettingsAsync()).ToDictionary(setting => setting.MachineId, StringComparer.Ordinal);
        var self = identity.Get();
        List<JobMachine> result = [await LocalAsync(self, settings.GetValueOrDefault(self.Id), ct)];

        foreach (var machine in await machines.ListAsync())
        {
            var setting = settings.GetValueOrDefault(machine.Id);
            var seats = setting?.Seats ?? 0;
            var labels = setting?.Labels ?? [];
            result.Add(seats == 0
                ? new JobMachine(machine.Id, machine.Name, false, machine.Status == RemoteMachineStatuses.Online, true, 0, labels, [])
                : await RemoteAsync(machine, seats, labels, ct));
        }

        return result;
    }

    private async Task<JobMachine> LocalAsync(MachineIdentity self, JobMachineSettings? setting, CancellationToken ct)
    {
        var folders = (await repositories.ScanRepositoriesAsync(ct))
            .Select(repository => new JobRepository(repository.Path, repository.Name, repository.RemoteUrl))
            .ToList();
        // Until the first check of the harnesses finishes there is nothing to say against starting one.
        var harnesses = (await capabilities.ReadAsync()).Harnesses;
        var ready = harnesses is null || harnesses.Any(harness => harness.Available && harness.Enabled);
        return new JobMachine(self.Id, self.Name ?? "This machine", true, true, ready, setting?.Seats ?? FleetJobs.DefaultLocalSeats, setting?.Labels ?? [], folders);
    }

    private async Task<JobMachine> RemoteAsync(RemoteMachine machine, int seats, IReadOnlyList<string> labels, CancellationToken ct)
    {
        var away = new JobMachine(machine.Id, machine.Name, false, false, false, seats, labels, []);
        var token = machines.TryTokenOf(machine);
        if (token is null)
            return away;

        var view = await remote.DescribeAsync(machine, token, ct);
        if (view.Away is not null)
            return away;

        var found = await remote.RepositoriesAsync(machine, token, ct);
        if (found is null)
            return away;

        List<JobRepository> folders = [];
        foreach (var (path, name) in found)
        {
            var key = $"{machine.Id}\n{path}";
            if (!cache.RemoteUrls.TryGetValue(key, out var url))
            {
                var read = await remote.RemoteUrlAsync(machine, token, path, ct);
                if (read is null)
                    return away;
                url = cache.RemoteUrls[key] = read;
            }

            folders.Add(new JobRepository(path, name, url));
        }

        return new JobMachine(machine.Id, machine.Name, false, true, view.Harnesses.Count > 0, seats, labels, folders);
    }
}

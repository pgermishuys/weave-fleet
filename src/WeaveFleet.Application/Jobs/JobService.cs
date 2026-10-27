using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Machines;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Jobs;

public sealed record PostJobRequest(string? Title, string? Ask, string? Repository, IReadOnlyList<string>? Labels, string? PinnedMachineId);

public sealed record SaveJobMachineRequest(int? Seats, IReadOnlyList<string>? Labels);

/// <param name="WhyWaiting">In plain words, why a waiting job hasn't started; null for any other job.</param>
public sealed record JobDto(
    string Id,
    string Title,
    string Ask,
    string Repository,
    IReadOnlyList<string> Labels,
    string? PinnedMachineId,
    string State,
    string? MachineId,
    string? MachineName,
    string? SessionId,
    string? WhyWaiting,
    string? Error,
    DateTimeOffset CreatedAt);

/// <param name="Used">Seats held by jobs that are running or need the user.</param>
/// <param name="Status">One of <see cref="RemoteMachineStatuses"/>; this machine is always online.</param>
public sealed record JobMachineDto(string Id, string Name, bool IsLocal, int Seats, int Used, IReadOnlyList<string> Labels, string Status);

public sealed record JobsDto(IReadOnlyList<JobDto> Jobs, IReadOnlyList<JobMachineDto> Machines);

/// <param name="Repository">What a job names: the origin remote, or the folder name when there is none.</param>
/// <param name="Machines">The machines that have it.</param>
public sealed record JobRepositoryDto(string Name, string Repository, IReadOnlyList<string> Machines);

/// <summary>Posting, listing and cancelling jobs, and each machine's seats and labels for them.</summary>
public sealed class JobService(
    IJobRepository jobs,
    IUserContext user,
    MachineIdentityStore identity,
    RemoteMachineService machines,
    JobMachineCache cache,
    IJobMachines jobMachines,
    JobDispatcher dispatcher,
    TimeProvider time)
{
    private const int MaxSeats = 16;

    public async Task<JobsDto> ListAsync()
    {
        var all = await jobs.ListAsync(user.UserId);
        var used = JobMatching.SeatsUsed(all);
        var settings = (await jobs.ListMachineSettingsAsync()).ToDictionary(setting => setting.MachineId, StringComparer.Ordinal);

        var self = identity.Get();
        var own = settings.GetValueOrDefault(self.Id);
        List<JobMachineDto> list =
        [
            new(self.Id, self.Name ?? "This machine", true, own?.Seats ?? FleetJobs.DefaultLocalSeats, used.GetValueOrDefault(self.Id), own?.Labels ?? [], RemoteMachineStatuses.Online),
        ];
        foreach (var machine in await machines.ListAsync())
        {
            var setting = settings.GetValueOrDefault(machine.Id);
            list.Add(new(machine.Id, machine.Name, false, setting?.Seats ?? 0, used.GetValueOrDefault(machine.Id), setting?.Labels ?? [], machine.Status));
        }

        // Jobs switched off and on again leave open jobs nothing wakes the dispatcher for: opening the screen does.
        if (all.Any(job => job.State is JobStates.Waiting || JobStates.HoldsSeat(job.State)))
            dispatcher.Nudge();

        var seen = cache.Last;
        return new JobsDto([.. all.Select(job => ToDto(job, seen, used))], list);
    }

    /// <summary>The repositories on the machines that take jobs, each once, for the picker in New job.</summary>
    public async Task<IReadOnlyList<JobRepositoryDto>> RepositoriesAsync(CancellationToken ct)
    {
        var machines = await jobMachines.ListAsync(ct);
        cache.Last = machines;
        return [.. machines
            .Where(machine => machine.Seats > 0)
            .SelectMany(machine => machine.Repositories.Select(repository => (Machine: machine.Name, Repository: repository)))
            .GroupBy(found => found.Repository.RemoteUrl.Length > 0 ? found.Repository.RemoteUrl : found.Repository.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => new JobRepositoryDto(JobMatching.RepositoryName(group.Key), group.Key, [.. group.Select(found => found.Machine).Distinct()]))
            .OrderBy(repository => repository.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<Result<JobDto>> PostAsync(PostJobRequest request)
    {
        var title = request.Title?.Trim();
        var ask = request.Ask?.Trim();
        var repository = request.Repository?.Trim();
        if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(ask) || string.IsNullOrEmpty(repository))
            return FleetError.ValidationError("Job", "A job needs a title, an ask and a repository.");

        var now = time.GetUtcNow();
        var job = new Job
        {
            Id = Guid.NewGuid().ToString("N"),
            UserId = user.UserId,
            Title = title,
            Ask = ask,
            Repository = repository,
            Labels = CleanLabels(request.Labels),
            PinnedMachineId = string.IsNullOrWhiteSpace(request.PinnedMachineId) ? null : request.PinnedMachineId.Trim(),
            CreatedAt = now,
            UpdatedAt = now,
        };
        await jobs.InsertAsync(job);
        dispatcher.Nudge();
        return ToDto(job, cache.Last, new Dictionary<string, int>());
    }

    /// <summary>Removes a job that still waits. A job that started is handled in its session.</summary>
    public Task<bool> CancelAsync(string id) => jobs.DeleteWaitingAsync(id, user.UserId);

    public async Task<Result<JobMachineDto>> SaveMachineAsync(string machineId, SaveJobMachineRequest request)
    {
        var self = identity.Get();
        var remote = machineId == self.Id ? null : await machines.GetAsync(machineId);
        if (machineId != self.Id && remote is null)
            return new FleetError("Machine.NotFound", "No such machine.");
        if (request.Seats is < 0 or > MaxSeats)
            return FleetError.ValidationError("Seats", $"Seats go from 0 to {MaxSeats}.");

        var current = (await jobs.ListMachineSettingsAsync()).FirstOrDefault(setting => setting.MachineId == machineId);
        var seats = request.Seats ?? current?.Seats ?? (remote is null ? FleetJobs.DefaultLocalSeats : 0);
        var labels = request.Labels is null ? current?.Labels ?? [] : CleanLabels(request.Labels);
        await jobs.SaveMachineSettingsAsync(new JobMachineSettings(machineId, seats, labels));
        dispatcher.Nudge();

        var used = JobMatching.SeatsUsed(await jobs.ListAsync(user.UserId));
        return new JobMachineDto(machineId, remote?.Name ?? self.Name ?? "This machine", remote is null, seats, used.GetValueOrDefault(machineId), labels, remote?.Status ?? RemoteMachineStatuses.Online);
    }

    private static JobDto ToDto(Job job, IReadOnlyList<JobMachine>? seen, IReadOnlyDictionary<string, int> used) => new(
        job.Id,
        job.Title,
        job.Ask,
        job.Repository,
        job.Labels,
        job.PinnedMachineId,
        job.State,
        job.MachineId,
        job.MachineName,
        job.SessionId,
        job.State != JobStates.Waiting ? null : seen is null ? "Fleet is still looking at the machines." : JobMatching.WhyWaiting(job, seen, used),
        job.Error,
        job.CreatedAt);

    private static List<string> CleanLabels(IReadOnlyList<string>? labels) =>
        [.. (labels ?? []).Select(label => label.Trim()).Where(label => label.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];
}

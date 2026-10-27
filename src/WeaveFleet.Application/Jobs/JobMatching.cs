using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Application.Jobs;

/// <summary>A machine as the matcher sees it: whether it answers, what it can run, and its seats and labels for jobs.</summary>
/// <param name="Answering">It answered when last asked.</param>
/// <param name="HasHarness">A harness is installed, working and on there, so a new session can start.</param>
public sealed record JobMachine(
    string Id,
    string Name,
    bool IsLocal,
    bool Answering,
    bool HasHarness,
    int Seats,
    IReadOnlyList<string> Labels,
    IReadOnlyList<JobRepository> Repositories);

/// <summary>A repository on a machine: where it is there, its folder name and its origin remote (empty when it has none).</summary>
public sealed record JobRepository(string Path, string Name, string RemoteUrl);

/// <summary>Which machine takes a job, and why a job waits. Pure: the same jobs and machines always give the same answer.</summary>
public static class JobMatching
{
    /// <summary>The seats each machine's running jobs hold.</summary>
    public static Dictionary<string, int> SeatsUsed(IEnumerable<Job> jobs) =>
        jobs.Where(job => JobStates.HoldsSeat(job.State) && job.MachineId is not null)
            .GroupBy(job => job.MachineId!)
            .ToDictionary(group => group.Key, group => group.Count());

    /// <summary>
    /// The machine that should take the job and its repository there: one that fits and has a free seat, this machine
    /// first, then the one with the most seats free.
    /// </summary>
    public static (JobMachine Machine, JobRepository Repository)? Pick(Job job, IReadOnlyList<JobMachine> machines, IReadOnlyDictionary<string, int> used)
    {
        var fits = machines
            .Where(machine => Fits(job, machine) && Free(machine, used) > 0)
            .OrderByDescending(machine => machine.IsLocal)
            .ThenByDescending(machine => Free(machine, used))
            .ThenBy(machine => machine.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        return fits is null ? null : (fits, RepositoryOn(job, fits)!);
    }

    /// <summary>Whether the machine could take the job if it had a seat: it takes jobs, answers, can start a session, and has the labels and the repository.</summary>
    public static bool Fits(Job job, JobMachine machine) =>
        machine.Seats > 0
        && machine.Answering
        && machine.HasHarness
        && (job.PinnedMachineId is null || job.PinnedMachineId == machine.Id)
        && HasLabels(job, machine)
        && RepositoryOn(job, machine) is not null;

    /// <summary>In plain words, why nothing took the job yet.</summary>
    public static string WhyWaiting(Job job, IReadOnlyList<JobMachine> machines, IReadOnlyDictionary<string, int> used)
    {
        if (job.PinnedMachineId is { } pinnedId)
        {
            var pinned = machines.FirstOrDefault(machine => machine.Id == pinnedId);
            if (pinned is null)
                return "It's pinned to a machine that isn't in the list.";
            if (!pinned.Answering || !pinned.HasHarness)
                return $"Pinned to {pinned.Name}, which isn't answering.";
            if (pinned.Seats == 0)
                return $"Pinned to {pinned.Name}, which takes no jobs.";
            machines = [pinned];
        }

        var taking = machines.Where(machine => machine.Seats > 0).ToList();
        if (taking.Count == 0)
            return "No machine takes jobs yet. Give one a seat in Settings.";

        var answering = taking.Where(machine => machine.Answering && machine.HasHarness).ToList();
        if (answering.Count == 0)
            return "No machine that takes jobs is answering.";

        var withRepository = answering.Where(machine => RepositoryOn(job, machine) is not null).ToList();
        if (withRepository.Count == 0)
            return $"No machine has {RepositoryName(job.Repository)}.";

        var withLabels = withRepository.Where(machine => HasLabels(job, machine)).ToList();
        if (withLabels.Count == 0)
            return $"No machine has {Quote(job.Labels)}.";

        if (job.Labels.Count > 0)
        {
            var names = string.Join(" and ", withLabels.Select(machine => machine.Name));
            return $"Needs {Quote(job.Labels)}: only {names} {(withLabels.Count == 1 ? "has" : "have")} it, and {(withLabels.Count == 1 ? "it's" : "they're")} full.";
        }

        return withLabels.Any(machine => Free(machine, used) > 0)
            ? "It starts on the next check."
            : "No machine has a free seat.";
    }

    /// <summary>The job's repository on the machine: the same origin remote, or the same folder name when the job names no remote.</summary>
    public static JobRepository? RepositoryOn(Job job, JobMachine machine)
    {
        var wanted = NormalizeRemote(job.Repository);
        return machine.Repositories.FirstOrDefault(repository =>
            repository.RemoteUrl.Length > 0
                ? wanted == NormalizeRemote(repository.RemoteUrl)
                : !LooksLikeRemote(job.Repository) && string.Equals(job.Repository, repository.Name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The repository's name to show: the last part of its remote, without <c>.git</c>, or the folder name.</summary>
    public static string RepositoryName(string repository)
    {
        var trimmed = repository.TrimEnd('/');
        var cut = trimmed.LastIndexOfAny(['/', ':']);
        var name = cut >= 0 ? trimmed[(cut + 1)..] : trimmed;
        return name.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    /// <summary>A remote without its scheme, user, <c>.git</c> and case, so ssh and https forms of one repository match.</summary>
    public static string NormalizeRemote(string remote)
    {
        var value = remote.Trim();
        var scheme = value.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
            value = value[(scheme + 3)..];
        var at = value.IndexOf('@', StringComparison.Ordinal);
        if (at >= 0)
            value = value[(at + 1)..];
        value = value.Replace(':', '/').TrimEnd('/');
        if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            value = value[..^4];
        return value.ToLowerInvariant();
    }

    private static bool LooksLikeRemote(string repository) => repository.Contains('/', StringComparison.Ordinal) || repository.Contains(':', StringComparison.Ordinal);

    private static bool HasLabels(Job job, JobMachine machine) =>
        job.Labels.All(label => machine.Labels.Contains(label, StringComparer.OrdinalIgnoreCase));

    private static int Free(JobMachine machine, IReadOnlyDictionary<string, int> used) =>
        machine.Seats - used.GetValueOrDefault(machine.Id);

    private static string Quote(IReadOnlyList<string> labels) => string.Join(" and ", labels.Select(label => $"`{label}`"));
}

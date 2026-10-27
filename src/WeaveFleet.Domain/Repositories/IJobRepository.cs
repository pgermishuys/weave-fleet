using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Domain.Repositories;

/// <summary>The <c>jobs</c> and <c>job_machine_settings</c> tables (migration 068).</summary>
public interface IJobRepository
{
    Task InsertAsync(Job job);

    /// <summary>The user's jobs, oldest first.</summary>
    Task<IReadOnlyList<Job>> ListAsync(string userId);

    /// <summary>The users who have a job that still waits or holds a seat.</summary>
    Task<IReadOnlyList<string>> ListUsersWithOpenJobsAsync();

    /// <summary>Hands a waiting job to the machine. False when it was no longer waiting, so nobody else starts it too.</summary>
    Task<bool> ClaimAsync(string id, string machineId, string machineName);

    Task SetSessionAsync(string id, string sessionId);

    /// <summary>Puts a claimed job back to waiting, with why it didn't start.</summary>
    Task ReleaseAsync(string id, string? reason);

    Task SetStateAsync(string id, string state, bool worked, string? reason);

    /// <summary>Removes the job if it's still waiting.</summary>
    Task<bool> DeleteWaitingAsync(string id, string userId);

    Task<IReadOnlyList<JobMachineSettings>> ListMachineSettingsAsync();

    Task SaveMachineSettingsAsync(JobMachineSettings settings);
}

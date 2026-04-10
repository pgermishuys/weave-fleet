using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Services;

/// <summary>
/// Instance lifecycle management — DB-side tracking of harness processes.
/// The actual process handles are managed by InstanceTracker (Phase 5).
/// </summary>
public sealed class InstanceService(
    IInstanceRepository instanceRepository,
    ISessionRepository sessionRepository,
    IUserContext userContext)
{
    public async Task<Result<Instance>> RegisterInstanceAsync(
        string id,
        int port,
        int? pid,
        string directory,
        string url)
    {
        var instance = new Instance
        {
            Id = id,
            Port = port,
            Pid = pid,
            Directory = directory,
            Url = url,
            Status = "running",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UserId = userContext.UserId
        };

        await instanceRepository.InsertAsync(instance);
        return instance;
    }

    public async Task<Result<Instance>> GetInstanceAsync(string id)
    {
        var instance = await instanceRepository.GetByIdAsync(id);
        if (instance is null)
            return FleetError.NotFoundFor(nameof(Instance), id);
        return instance;
    }

    public async Task<Result<IReadOnlyList<Instance>>> ListInstancesAsync()
    {
        var instances = await instanceRepository.ListAsync();
        return Result.Success(instances);
    }

    public async Task<Result<IReadOnlyList<Instance>>> GetRunningInstancesAsync()
    {
        var instances = await instanceRepository.GetRunningAsync();
        return Result.Success(instances);
    }

    public async Task<Result<Unit>> UpdateInstanceStatusAsync(string id, string status, string? stoppedAt = null)
    {
        var instance = await instanceRepository.GetByIdAsync(id);
        if (instance is null)
            return FleetError.NotFoundFor(nameof(Instance), id);

        await instanceRepository.UpdateStatusAsync(id, status, stoppedAt);
        return Unit.Value;
    }

    /// <summary>
    /// Recovery: marks all running instances as stopped. Called at startup to reset stale state.
    /// </summary>
    public async Task<int> MarkAllStoppedAsync()
    {
        var stoppedAt = DateTime.UtcNow.ToString("O");
        return await instanceRepository.MarkAllStoppedAsync(stoppedAt);
    }

    /// <summary>
    /// Recovery: marks all non-terminal sessions as stopped. Called at startup after instances are stopped.
    /// </summary>
    public async Task<int> MarkAllNonTerminalSessionsStoppedAsync()
    {
        var stoppedAt = DateTime.UtcNow.ToString("O");
        return await sessionRepository.MarkAllNonTerminalStoppedAsync(stoppedAt);
    }
}

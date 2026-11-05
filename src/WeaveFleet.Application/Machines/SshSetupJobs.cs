using System.Collections.Concurrent;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Application.Machines;

/// <summary>One machine being set up from here: its steps so far and, when it ends, the machine or why not.</summary>
public sealed class SshSetupJob(string id, string destination)
{
    public string Id { get; } = id;
    public string Destination { get; } = destination;
    public SetupProgress Progress { get; } = new();
    public DateTimeOffset? FinishedAt { get; private set; }
    public SshAddResult? Result { get; private set; }

    /// <summary><c>running</c> until the set-up ends, then <c>done</c> or <c>failed</c>.</summary>
    public string Status => Result is null ? "running" : Result.Machine is null ? "failed" : "done";

    internal void End(SshAddResult result, DateTimeOffset now)
    {
        Result = result;
        FinishedAt = now;
    }
}

/// <summary>
/// Sets-up that are running or just ended, kept in memory so the dialog can poll their steps. A set-up outlives the
/// request that started it, so closing the dialog doesn't stop a half-installed Fleet.
/// </summary>
public sealed class SshSetupJobs(SshMachineService ssh, TimeProvider? time = null)
{
    private static readonly TimeSpan Keep = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, SshSetupJob> _jobs = new(StringComparer.Ordinal);

    /// <summary>Starts setting <paramref name="destination"/> up; a set-up already running for it is returned instead.</summary>
    public SshSetupJob Start(string destination)
    {
        var now = _time.GetUtcNow();
        foreach (var old in _jobs.Values.Where(j => j.FinishedAt is { } at && now - at > Keep))
            _jobs.TryRemove(old.Id, out _);

        var running = _jobs.Values.FirstOrDefault(j => j.Result is null && j.Destination == destination);
        if (running is not null)
            return running;

        var job = new SshSetupJob(Guid.NewGuid().ToString("N"), destination);
        _jobs[job.Id] = job;
        _ = Task.Run(async () =>
        {
            SshAddResult result;
            try
            {
                result = await ssh.SetUpAsync(destination, job.Progress, CancellationToken.None);
            }
            catch (Exception ex)
            {
                result = SshAddResult.Failed($"Setting up {destination} stopped: {ex.Message}");
            }

            job.End(result, _time.GetUtcNow());
        });
        return job;
    }

    public SshSetupJob? Get(string id) => _jobs.GetValueOrDefault(id);
}

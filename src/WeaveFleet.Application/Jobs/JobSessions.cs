using System.Text.Json;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Machines;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Application.Sessions.Creation;
using WeaveFleet.Application.SessionSources;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Jobs;

/// <summary>How a job's session stands.</summary>
public enum JobSessionState
{
    /// <summary>It isn't on the machine any more.</summary>
    Gone,
    InTurn,
    WaitingInput,

    /// <summary>Between turns, and it has answered.</summary>
    Replied,

    /// <summary>Between turns and it hasn't answered yet: it was just created, and a new session reads idle before its first turn starts.</summary>
    Quiet,
}

/// <summary>Starts a job's session on a machine, and reads how it stands.</summary>
public interface IJobSessions
{
    /// <summary>Starts the job's session in a new worktree of the repository on the machine, with the ask as its prompt. The session's id, or why it didn't start.</summary>
    Task<(string? SessionId, string? Error)> StartAsync(Job job, JobMachine machine, JobRepository repository, CancellationToken ct);

    /// <summary>How the job's session stands; null when that can't be told now (the machine didn't answer).</summary>
    Task<JobSessionState?> ReadAsync(Job job, CancellationToken ct);
}

/// <summary>
/// Starts job sessions through the paths Fleet already has: the session creation here, and a machine's
/// <c>POST /api/sessions</c> with the token kept for it, as an automation on a machine does.
/// </summary>
public sealed class JobSessions(
    SessionCreation sessionCreation,
    ISessionRepository sessions,
    IMessageRepository messages,
    SessionActivityTracker activity,
    MachineIdentityStore identity,
    RemoteMachineService machines,
    RemoteSessions remote) : IJobSessions
{
    /// <summary>What a job's session carries as its source reference, so its session says where it came from.</summary>
    public const string SourceReferencePrefix = "job:";

    public async Task<(string? SessionId, string? Error)> StartAsync(Job job, JobMachine machine, JobRepository repository, CancellationToken ct)
    {
        if (machine.IsLocal)
        {
            var result = await sessionCreation.CreateSessionAsync(
                new CreateSessionRequest
                {
                    Title = job.Title,
                    InitialPrompt = job.Ask,
                    Source = WorktreeSource(repository.Path),
                    SourceReference = $"{SourceReferencePrefix}{job.Id}",
                },
                ct);
            return result.IsSuccess
                ? (result.Value.Session.Id, null)
                : (null, $"Couldn't start: {result.Error.Description}");
        }

        var found = await machines.GetAsync(machine.Id);
        var token = found is null ? null : machines.TryTokenOf(found);
        if (found is null || token is null)
            return (null, $"Fleet can't read its token for {machine.Name}.");

        return await remote.StartWorktreeAsync(found, token, repository.Path, job.Title, job.Ask, ct);
    }

    public async Task<JobSessionState?> ReadAsync(Job job, CancellationToken ct)
    {
        if (job.SessionId is null || job.MachineId is null)
            return null;

        if (job.MachineId == identity.Get().Id)
        {
            var session = await sessions.GetByIdAsync(job.SessionId);
            if (session is null || session.RetentionStatus == "archived")
                return JobSessionState.Gone;

            var status = activity.GetEffectiveActivityStatus(job.SessionId);
            if (status == ActivityStatuses.WaitingInput)
                return JobSessionState.WaitingInput;
            if (SessionActivityTracker.IsInTurn(status))
                return JobSessionState.InTurn;

            var page = await messages.GetBySessionAsync(job.SessionId, 30, null);
            return page.Any(message => message.Role == "assistant") ? JobSessionState.Replied : JobSessionState.Quiet;
        }

        var machine = await machines.GetAsync(job.MachineId);
        var token = machine is null ? null : machines.TryTokenOf(machine);
        if (machine is null || token is null)
            return null;

        return await remote.StateAsync(machine, token, job.SessionId, ct) switch
        {
            RemoteSessionState.InTurn => JobSessionState.InTurn,
            RemoteSessionState.WaitingInput => JobSessionState.WaitingInput,
            RemoteSessionState.Replied => JobSessionState.Replied,
            RemoteSessionState.Quiet => JobSessionState.Quiet,
            _ => null,
        };
    }

    /// <summary>A new worktree of the repository, from its default branch, named the way Fleet names worktrees.</summary>
    private static SessionSourceSelection WorktreeSource(string repositoryPath)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("repositoryPath", repositoryPath);
            writer.WriteString("isolationStrategy", "worktree");
            writer.WriteEndObject();
        }

        return new SessionSourceSelection
        {
            Key = SessionSourceCatalog.RepositoryStartSession.Key,
            Input = JsonDocument.Parse(buffer.ToArray()).RootElement.Clone(),
        };
    }
}

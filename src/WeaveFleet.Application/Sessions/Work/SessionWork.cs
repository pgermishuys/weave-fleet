using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Sessions.Work;

/// <summary>
/// What the user can do with work an agent left running: stop one piece of it, or read its output. Both go to the
/// session's live harness (<see cref="IHarnessSession.StopWorkAsync"/>, <see cref="IHarnessSession.ReadWorkOutputAsync"/>);
/// work runs in the harness, so a session that isn't running has none left to act on.
/// </summary>
public sealed class SessionWork(
    ISessionRepository sessionRepository,
    InstanceTracker instanceTracker,
    DelegationService delegationService)
{
    /// <summary>
    /// Stops work item <paramref name="itemId"/> of session <paramref name="sessionId"/>, leaving the session and its other
    /// work running. The item ends cancelled at once; the harness's own report of the end changes nothing after that.
    /// </summary>
    public async Task<Result<RunningWorkItem>> StopWorkAsync(string sessionId, string itemId, CancellationToken ct = default)
    {
        var target = await FindLiveWorkAsync(sessionId, itemId, w => w.CanStop, "Fleet can't stop this on its own: its session's harness doesn't offer it.")
            .ConfigureAwait(false);
        if (target.IsFailure)
            return target.Error;

        var (work, instance) = target.Value;
        if (!work.IsRunning)
            return new FleetError("General.Conflict", "It has already finished.");

        bool stopped;
        try
        {
            stopped = await instance.StopWorkAsync(work.WorkId, ct).ConfigureAwait(false);
        }
        catch (NotSupportedException ex)
        {
            return FleetError.ValidationError("Work.Stop", ex.Message);
        }

        // Not there any more: it ended without Fleet hearing how.
        var ended = await delegationService.HandleWorkEndedAsync(
                sessionId, work.WorkId, stopped ? WorkEndedReasons.Cancelled : WorkEndedReasons.Lost, stopped ? "stopped" : null)
            .ConfigureAwait(false);
        return ended ?? DelegationService.ToItem(work);
    }

    /// <summary>
    /// A page of work item <paramref name="itemId"/>'s output from byte <paramref name="offset"/>. Readable after the work
    /// ended too, for as long as the harness keeps it.
    /// </summary>
    public async Task<Result<WorkOutput>> ReadWorkOutputAsync(string sessionId, string itemId, long offset, CancellationToken ct = default)
    {
        if (offset < 0)
            return FleetError.ValidationError("Work.Offset", "offset can't be negative.");

        var target = await FindLiveWorkAsync(sessionId, itemId, w => w.CanReadOutput, "Fleet can't read this one's output: its session's harness doesn't offer it.")
            .ConfigureAwait(false);
        if (target.IsFailure)
            return target.Error;

        var (work, instance) = target.Value;
        WorkOutput? output;
        try
        {
            output = await instance.ReadWorkOutputAsync(work.WorkId, offset, ct).ConfigureAwait(false);
        }
        catch (NotSupportedException ex)
        {
            return FleetError.ValidationError("Work.Output", ex.Message);
        }

        return output is null
            ? FleetError.NotFoundFor("WorkOutput", itemId)
            : output;
    }

    /// <summary>The work item, when the session has it and the harness can do what's asked, and the session's live harness.</summary>
    private async Task<Result<(Delegation Work, IHarnessSession Instance)>> FindLiveWorkAsync(
        string sessionId,
        string itemId,
        Func<Delegation, bool> allowed,
        string notAllowed)
    {
        var session = await sessionRepository.GetSessionAsync(sessionId).ConfigureAwait(false);
        if (session.IsFailure)
            return session.Error;

        var work = await delegationService.FindWorkAsync(sessionId, itemId).ConfigureAwait(false);
        if (work is null)
            return FleetError.NotFoundFor("Work", itemId);
        if (!allowed(work))
            return FleetError.ValidationError("Work", notAllowed);

        if (instanceTracker.Get(session.Value.InstanceId) is not { } instance)
            return new FleetError("General.Conflict", "Its session isn't running, so neither is the work.");

        return (work, instance);
    }
}

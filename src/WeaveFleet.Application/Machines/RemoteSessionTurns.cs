using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Events;

namespace WeaveFleet.Application.Machines;

/// <summary>
/// Follows a session on another machine while a session here waits to hear from it (<c>notifyWhenDone</c>): this Fleet
/// subscribes to that session's events over the connection it keeps to the machine, and stops once nobody waits.
/// </summary>
public interface IRemoteSessionEvents
{
    void Follow(string machineId, string sessionId);
}

/// <summary>
/// What a followed session on another machine did, fed to <see cref="SessionUpdates"/> as if it were a session here: its
/// reply naming our message arms the watch, a failed turn says why, and going idle sends the update. Its events come over
/// the machine's hub, so a turn that ended while that connection was down is asked about instead
/// (<see cref="CatchUpAsync"/>): nobody waits for an event that already happened.
/// </summary>
public sealed class RemoteSessionTurns(SessionUpdates updates, RemoteSessions remote)
{
    /// <summary>An event from the followed session. True once nobody waits on the session any more.</summary>
    public bool Observe(string sessionId, DomainEvent domainEvent)
    {
        updates.Observe(sessionId, domainEvent);
        return domainEvent is SessionIdled && !updates.IsWatching(sessionId);
    }

    /// <summary>
    /// Asks the machine about each awaited message once its events are flowing again (subscribed, or back after a gap).
    /// A turn there that already ended counts as if its events had come: its reply, its failure, and idle. True once
    /// nobody waits on the session any more.
    /// </summary>
    public async Task<bool> CatchUpAsync(string machineId, string sessionId, CancellationToken ct = default)
    {
        var awaited = updates.AwaitedMessages(sessionId);
        if (awaited.Count == 0)
            return true;

        var (machine, token, _) = await remote.FindAsync(machineId).ConfigureAwait(false);
        if (machine is null)
            return false;

        var ended = false;
        foreach (var messageId in awaited)
        {
            var turn = await remote.TurnAsync(machine, token!, sessionId, messageId, ct).ConfigureAwait(false);
            if (turn is not { Ended: true })
                continue;

            ended = true;
            updates.Observe(sessionId, new MessageUpdated
            {
                Payload = new MessageLifecyclePayload
                {
                    Info = new MessageEventInfo
                    {
                        Id = turn.ReplyId ?? $"{messageId}-reply",
                        Role = "assistant",
                        SessionId = sessionId,
                        ParentId = messageId,
                        Time = new MessageEventTime { Created = 0 },
                    },
                },
            });
            if (turn.Failure is { } failure)
                updates.Observe(sessionId, new TurnFailed { Payload = new TurnFailedPayload { SessionId = sessionId, Error = failure } });
        }

        return ended && Observe(sessionId, new SessionIdled { Payload = new SessionIdledPayload { SessionId = sessionId } });
    }
}

using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Application.Machines;

/// <summary>
/// The hand-offs waiting for the user's answer: an agent sends work to a machine set to Ask, and Fleet asks in the
/// conversation, the way a harness's permission ask shows (<c>permission.asked</c>, with <see cref="PermissionAsk.Handoff"/>
/// set). The tool call waits for the answer. Each wait is keyed by the call's own arguments, so the plugin asking again
/// every <see cref="Round"/> (OpenCode cuts a request held for about five minutes), or the agent calling again, finds the
/// same ask instead of raising a second. While one waits, the session waits on the user (<c>waiting_input</c>).
/// A wait ends when the user answers, the turn stops, the harness process goes away or Fleet shuts down.
/// </summary>
public sealed class MachineHandoffAsks
{
    /// <summary>What every ask Fleet raises for a hand-off starts with, so its answer comes here and not to a harness.</summary>
    public const string IdPrefix = "fleet-handoff-";

    public static readonly TimeSpan Round = TimeSpan.FromSeconds(50);

    public static readonly TimeSpan Cap = TimeSpan.FromHours(24);

    private sealed record Wait(PermissionAsk Ask, string MachineId, DateTimeOffset Expires)
    {
        public TaskCompletionSource<string> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly ConcurrentDictionary<(string Session, string Key), Wait> _waits = new();

    // The sessions on other machines each session here started or messaged: messaging them again doesn't ask. In memory
    // only, so after a restart the first message asks again.
    private readonly ConcurrentDictionary<(string Caller, string Machine, string Session), byte> _granted = new();
    private readonly FleetSessionEvents _events;
    private readonly SessionActivityTracker _activity;
    private readonly TimeProvider _clock;

    public MachineHandoffAsks(FleetSessionEvents events, SessionActivityTracker activity, TimeProvider clock, IHostApplicationLifetime? lifetime = null)
    {
        _events = events;
        _activity = activity;
        _clock = clock;
        lifetime?.ApplicationStopping.Register(() =>
        {
            foreach (var wait in _waits.Values)
                End(wait, PermissionReplies.Gone);
        });
    }

    /// <summary>The machine setting an answer saves: Always for "Always for it", Never for "Not it"; null for once.</summary>
    public static string? SettingFor(string reply) => reply switch
    {
        PermissionReplies.Always => Domain.Entities.MachineAgentWork.Always,
        PermissionReplies.Reject => Domain.Entities.MachineAgentWork.Never,
        _ => null,
    };

    /// <summary>Whether <paramref name="askId"/> is an ask Fleet raised for a hand-off.</summary>
    public static bool Owns(string? askId) => askId?.StartsWith(IdPrefix, StringComparison.Ordinal) == true;

    /// <summary>Remembers that session <paramref name="caller"/> started or messaged <paramref name="sessionId"/> on the machine.</summary>
    public void Grant(string caller, string machineId, string sessionId) => _granted.TryAdd((caller, machineId, sessionId), 0);

    /// <summary>Whether session <paramref name="caller"/> started or messaged <paramref name="sessionId"/> on the machine.</summary>
    public bool IsGranted(string caller, string machineId, string sessionId) => _granted.ContainsKey((caller, machineId, sessionId));

    /// <summary>Whether the call keyed <paramref name="key"/> already waits in the session.</summary>
    public bool IsWaiting(string sessionId, string key) => _waits.ContainsKey((sessionId, key));

    /// <summary>
    /// Shows <paramref name="ask"/> in its session and starts the wait for its answer. False when nothing serves the
    /// session, so nobody could see it.
    /// </summary>
    public bool Ask(string key, string machineId, PermissionAsk ask)
    {
        var wait = new Wait(ask, machineId, _clock.GetUtcNow() + Cap);
        if (!_waits.TryAdd((ask.SessionId, key), wait))
            return true;
        if (!_events.RaisePermissionAsked(ask))
        {
            _waits.TryRemove((ask.SessionId, key), out _);
            return false;
        }

        _events.RaiseStatus(ask.SessionId, ActivityStatuses.WaitingInput);
        return true;
    }

    /// <summary>
    /// The answer (<see cref="PermissionReplies"/>) when it comes within <paramref name="round"/>, or null when the call
    /// still waits. An answer ends the wait. A call that goes away ends it too, as <see cref="PermissionReplies.Gone"/>:
    /// the turn stopped, and the card goes with it.
    /// </summary>
    public async Task<string?> NextAsync(string sessionId, string key, TimeSpan round, CancellationToken ct)
    {
        if (!_waits.TryGetValue((sessionId, key), out var wait))
            return PermissionReplies.Gone;

        var left = wait.Expires - _clock.GetUtcNow();
        try
        {
            if (left > TimeSpan.Zero)
                await Task.WhenAny(wait.Answer.Task, Task.Delay(left < round ? left : round, _clock, ct)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            End(wait, PermissionReplies.Gone);
            _waits.TryRemove((sessionId, key), out _);
            throw;
        }

        if (_clock.GetUtcNow() >= wait.Expires)
            End(wait, PermissionReplies.Gone);
        if (!wait.Answer.Task.IsCompleted)
            return null;

        _waits.TryRemove((sessionId, key), out _);
        return await wait.Answer.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// The user's answer to ask <paramref name="askId"/> in session <paramref name="sessionId"/>: the machine it's about,
    /// or null when no such ask waits (answered already, or gone).
    /// </summary>
    public (string MachineId, string Name)? Answer(string sessionId, string askId, string reply)
    {
        var wait = _waits.Values.FirstOrDefault(wait => wait.Ask.SessionId == sessionId && wait.Ask.Id == askId);
        return wait is not null && End(wait, reply) ? (wait.MachineId, wait.Ask.Handoff?.Machine ?? wait.MachineId) : null;
    }

    /// <summary>Ends every wait in the session: the user stopped the turn, or its harness process went away.</summary>
    public void EndAll(string sessionId)
    {
        foreach (var (key, wait) in _waits.Where(pair => pair.Key.Session == sessionId).ToList())
        {
            End(wait, PermissionReplies.Gone);
            _waits.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Settles the wait with <paramref name="reply"/>, once: its card goes, and the session works again unless another
    /// ask still waits there.
    /// </summary>
    private bool End(Wait wait, string reply)
    {
        if (!wait.Answer.TrySetResult(reply))
            return false;

        var session = wait.Ask.SessionId;
        _events.RaisePermissionReplied(wait.Ask, reply);
        // Only over Fleet's own status: whatever the harness said since is newer.
        if (!_waits.Values.Any(other => other != wait && other.Ask.SessionId == session && !other.Answer.Task.IsCompleted)
            && _activity.Get(session)?.ActivityStatus == ActivityStatuses.WaitingInput)
        {
            _events.RaiseStatus(session, ActivityStatuses.Busy);
        }

        return true;
    }
}

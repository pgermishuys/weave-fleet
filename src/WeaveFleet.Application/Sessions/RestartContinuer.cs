using Microsoft.Extensions.Logging;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Application.Sessions;

/// <summary>
/// The sessions a restart cut off that Fleet continues by itself (the user turned "Continue cut-off sessions by itself"
/// on), and the pace it does it at: <see cref="Concurrency"/> at a time, the next one starting when a continued session's
/// turn starts. A session stays in the pending set, and its Interrupted card keeps saying so, until its message is sent;
/// <see cref="Stop"/> empties the set, which leaves the cards with Continue.
/// </summary>
public sealed partial class RestartContinuer(ILogger<RestartContinuer> logger)
{
    /// <summary>How many sent messages may be waiting for their turn to start.</summary>
    public const int Concurrency = 2;

    /// <summary>Only a session cut off less than this long ago is continued by itself.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(2);

    /// <summary>The preference (per user) that turns the automatic continue on.</summary>
    public const string PreferenceKey = "RestartContinueAuto";

    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource> _awaitingStart = new(StringComparer.Ordinal);

    /// <summary>How long a sent message may take to start its turn before the next one goes anyway.</summary>
    internal TimeSpan StartTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public bool IsPending(string sessionId)
    {
        lock (_gate)
            return _pending.ContainsKey(sessionId);
    }

    /// <summary>Whether Fleet would continue this cut-off session by itself.</summary>
    public static bool Qualifies(SessionInterruption interruption, Session session, DateTimeOffset now, bool folderExists, bool harnessReady) =>
        !interruption.Waiting
        && now - interruption.At < MaxAge
        && !string.Equals(session.RetentionStatus, "archived", StringComparison.Ordinal)
        && folderExists
        && harnessReady;

    /// <summary>The session left the set some other way: the user continued, dismissed or prompted it.</summary>
    public void Forget(string sessionId)
    {
        lock (_gate)
            _pending.Remove(sessionId);
    }

    /// <summary>Called when a session's turn starts, which frees the slot its continue message held.</summary>
    public void NotifyTurnStarted(string sessionId)
    {
        TaskCompletionSource? waiting;
        lock (_gate)
            _awaitingStart.Remove(sessionId, out waiting);
        waiting?.TrySetResult();
    }

    /// <summary>Stop all: nothing not yet sent is sent. Returns the sessions that were waiting.</summary>
    public IReadOnlyList<string> Stop(string userId)
    {
        lock (_gate)
        {
            var stopped = _pending.Where(pair => pair.Value == userId).Select(pair => pair.Key).ToList();
            foreach (var id in stopped)
                _pending.Remove(id);
            return stopped;
        }
    }

    /// <summary>
    /// Marks the sessions pending the moment the restart is recognised, before the grace period, so every list says Fleet
    /// is continuing them and Stop all has something to stop.
    /// </summary>
    public void Begin(string userId, IReadOnlyList<string> sessionIds)
    {
        lock (_gate)
        {
            foreach (var id in sessionIds)
                _pending[id] = userId;
        }
    }

    /// <summary>Sends the sessions <see cref="Begin"/> marked and Stop all hasn't taken, at the pace above. <paramref name="send"/> is true when the message went.</summary>
    public async Task RunAsync(
        IReadOnlyList<string> sessionIds,
        Func<string, CancellationToken, Task<bool>> send,
        CancellationToken ct)
    {
        using var slots = new SemaphoreSlim(Concurrency);
        var releases = new List<Task>();
        foreach (var id in sessionIds)
        {
            await slots.WaitAsync(ct).ConfigureAwait(false);
            TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            bool take;
            lock (_gate)
            {
                // Stop all (or the user) may have taken it while it waited for a slot.
                take = _pending.Remove(id);
                if (take)
                    _awaitingStart[id] = started;
            }

            if (!take)
            {
                slots.Release();
                continue;
            }

            bool sent;
            try
            {
                sent = await send(id, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogSendFailed(ex, id);
                sent = false;
            }

            if (!sent)
            {
                lock (_gate)
                    _awaitingStart.Remove(id);
                slots.Release();
                continue;
            }

            releases.Add(ReleaseWhenStartedAsync(id, started, slots, ct));
        }

        await Task.WhenAll(releases).ConfigureAwait(false);
    }

    private async Task ReleaseWhenStartedAsync(string id, TaskCompletionSource started, SemaphoreSlim slots, CancellationToken ct)
    {
        try
        {
            await started.Task.WaitAsync(StartTimeout, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // The next one goes anyway: a turn that never starts can't hold the rest.
        }
        finally
        {
            lock (_gate)
                _awaitingStart.Remove(id);
            try
            {
                slots.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not continue session {SessionId} by itself after the restart")]
    private partial void LogSendFailed(Exception ex, string sessionId);
}

using Microsoft.Extensions.Logging;

namespace WeaveFleet.Application.Harnesses;

/// <summary>What Fleet last found when it checked every harness, and when that check started.</summary>
public sealed record HarnessAvailabilitySnapshot(IReadOnlyList<HarnessInfo> Harnesses, DateTimeOffset CheckedAt);

/// <summary>
/// Keeps the last answer from <see cref="IHarnessRegistry.GetAvailabilityAsync"/>, so the harness list doesn't wait for
/// every harness to run (<c>opencode --version</c> alone takes most of a second) each time a page asks for it.
/// <para>
/// An answer older than <see cref="FreshFor"/> is still served, and checked again in the background: the next read gets
/// the new one. A read that asks for a fresh answer (Check again, or a client waiting for an install) waits for a check
/// of its own, while other readers keep getting the kept answer. <see cref="Forget"/> makes every read wait for a new
/// check, for changes Fleet makes itself.
/// </para>
/// </summary>
public sealed partial class HarnessAvailabilityCache(
    IHarnessRegistry registry,
    TimeProvider time,
    ILogger<HarnessAvailabilityCache> logger)
{
    /// <summary>How long an answer is served without checking again; a harness installed from a terminal shows up within it.</summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(1);

    private sealed record Check(Task<HarnessAvailabilitySnapshot> Answer, long Generation);

    private readonly Lock _gate = new();
    private HarnessAvailabilitySnapshot? _last;
    // Bumped by Forget: a check started before it can't answer a read made after it.
    private long _generation;
    private long _lastGeneration = -1;
    private Check? _running;

    /// <summary>
    /// The harnesses as last checked. The first read, a read after <see cref="Forget"/> and a <paramref name="fresh"/>
    /// read wait for a check; otherwise the kept answer comes back at once, and an old one is checked again behind it.
    /// </summary>
    public Task<HarnessAvailabilitySnapshot> GetAsync(bool fresh, CancellationToken ct)
    {
        Task<HarnessAvailabilitySnapshot> answer;
        lock (_gate)
        {
            if (!fresh && _last is { } last && _lastGeneration == _generation)
            {
                if (time.GetUtcNow() - last.CheckedAt >= FreshFor && _running is null)
                    StartCheck();
                return Task.FromResult(last);
            }

            // A fresh read can't share a check that started before it asked: that one may miss what just changed.
            answer = !fresh && _running is { } running && running.Generation == _generation
                ? running.Answer
                : StartCheck();
        }

        // The check carries on for the other readers if this one goes away.
        return answer.WaitAsync(ct);
    }

    /// <summary>
    /// The kept answer, however old, without starting a check; <see langword="null"/> until the first check finishes.
    /// For readers that are asked often and mustn't start harness processes, like <c>GET /api/machine</c>.
    /// </summary>
    public HarnessAvailabilitySnapshot? Last
    {
        get
        {
            lock (_gate)
                return _last;
        }
    }

    /// <summary>Makes the next read wait for a new check: for changes Fleet makes to a harness (an update, say).</summary>
    public void Forget()
    {
        lock (_gate)
            _generation++;
    }

    /// <summary>Starts a check for the current generation; the caller holds <see cref="_gate"/>.</summary>
    private Task<HarnessAvailabilitySnapshot> StartCheck()
    {
        var answer = new TaskCompletionSource<HarnessAvailabilitySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var check = new Check(answer.Task, _generation);
        _running = check;
        // Off the lock and off the caller's thread: the probes start processes.
        _ = Task.Run(() => RunAsync(check, answer));
        return answer.Task;
    }

    private async Task RunAsync(Check check, TaskCompletionSource<HarnessAvailabilitySnapshot> answer)
    {
        var startedAt = time.GetUtcNow();
        try
        {
            // Not a reader's token: readers share this check, and the answer is kept for the ones that come later.
            var harnesses = await registry.GetAvailabilityAsync(CancellationToken.None).ConfigureAwait(false);
            var snapshot = new HarnessAvailabilitySnapshot(harnesses, startedAt);
            lock (_gate)
            {
                // Checks can overlap (a fresh read starts its own); an older one finishing later mustn't win.
                if (check.Generation > _lastGeneration || (check.Generation == _lastGeneration && startedAt >= _last!.CheckedAt))
                {
                    _last = snapshot;
                    _lastGeneration = check.Generation;
                }
                if (ReferenceEquals(_running, check))
                    _running = null;
            }
            answer.SetResult(snapshot);
        }
        catch (Exception ex)
        {
            LogCheckFailed(logger, ex);
            lock (_gate)
            {
                if (ReferenceEquals(_running, check))
                    _running = null;
            }
            answer.SetException(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Checking the harnesses failed")]
    private static partial void LogCheckFailed(ILogger logger, Exception exception);
}

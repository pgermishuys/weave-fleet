using System.Collections.Concurrent;
using WeaveFleet.Application.Canvases;

namespace WeaveFleet.Application.Machines;

/// <summary>
/// The sessions agents started on other machines in the last few minutes, by what was asked. An agent whose
/// <c>fleet_session_start</c> call timed out asks again; the second ask gets the first one's answer (waiting for it if it's
/// still running) instead of making a second session. Only a start that worked is kept, so a failed one can be retried.
/// </summary>
public sealed class RecentSessionStarts(TimeProvider? clock = null)
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, (Lazy<Task<CanvasResult<CanvasToolOutput>>> Start, DateTimeOffset At)> _starts = new();

    public async Task<CanvasResult<CanvasToolOutput>> OnceAsync(string key, Func<Task<CanvasResult<CanvasToolOutput>>> start)
    {
        var now = _clock.GetUtcNow();
        foreach (var old in _starts.Where(e => now - e.Value.At > Window).Select(e => e.Key))
            _starts.TryRemove(old, out _);

        var entry = _starts.GetOrAdd(key, _ => (new Lazy<Task<CanvasResult<CanvasToolOutput>>>(start), now));
        var result = await entry.Start.Value.ConfigureAwait(false);
        if (!result.IsSuccess)
            _starts.TryRemove(new KeyValuePair<string, (Lazy<Task<CanvasResult<CanvasToolOutput>>>, DateTimeOffset)>(key, entry));
        return result;
    }
}

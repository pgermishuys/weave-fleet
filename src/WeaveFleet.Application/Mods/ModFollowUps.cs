using System.Collections.Concurrent;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Application.Mods;

/// <summary>
/// What <c>$.prompt.submit</c> does in Fleet (<c>docs/mods/api.md</c>): queues the mod's message in the session's queue,
/// and keeps mods from talking to themselves. Mods may queue at most <see cref="MaxInARow"/> messages for a session
/// before the user sends or queues one of their own.
/// </summary>
public sealed class ModFollowUps
{
    /// <summary>How many follow-ups mods queue for a session before Fleet waits for the user.</summary>
    public const int MaxInARow = 3;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, int> _streak = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _dispatching = new(StringComparer.Ordinal);

    /// <summary>
    /// Marks the session's <c>turn.complete</c> dispatch as running until the result is disposed. A follow-up queued in
    /// that time is only added: the dispatcher sends the queue right after, so the message goes next and nothing
    /// is sent twice.
    /// </summary>
    internal IDisposable BeginTurnDispatch(string sessionId)
    {
        _dispatching.AddOrUpdate(sessionId, 1, (_, n) => n + 1);
        return new Window(this, sessionId);
    }

    /// <summary>The user sent or queued a prompt of their own in the session: the count starts again.</summary>
    public void UserSpoke(string sessionId)
    {
        lock (_gate)
            _streak.Remove(sessionId);
    }

    /// <summary>Queues a mod's message. Returns Fleet's reason when it is refused, else null.</summary>
    public async Task<string?> QueueAsync(PromptQueueService queue, string sessionId, string? text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "Type some text to queue.";
        if (text.Length > PromptQueueService.MaxTextLength)
            return $"The message is longer than {PromptQueueService.MaxTextLength} characters.";

        lock (_gate)
        {
            var count = _streak.GetValueOrDefault(sessionId);
            if (count >= MaxInARow)
                return $"Mods have queued {MaxInARow} follow-ups in a row in this session; Fleet waits for you before another.";
            _streak[sessionId] = count + 1;
        }

        var request = new QueuePromptRequest(text, QueuedPromptKinds.Prompt);
        var result = await (_dispatching.ContainsKey(sessionId) ? queue.EnqueueWithoutSendingAsync(sessionId, request, ct) : queue.EnqueueAsync(sessionId, request, ct)).ConfigureAwait(false);
        if (result.IsFailure)
        {
            lock (_gate)
            {
                if (_streak.TryGetValue(sessionId, out var count))
                    _streak[sessionId] = Math.Max(0, count - 1);
            }

            return result.Error.Description;
        }

        return null;
    }

    private sealed class Window(ModFollowUps owner, string sessionId) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            if (owner._dispatching.AddOrUpdate(sessionId, 0, (_, n) => n - 1) <= 0)
                owner._dispatching.TryRemove(new KeyValuePair<string, int>(sessionId, 0));
        }
    }
}

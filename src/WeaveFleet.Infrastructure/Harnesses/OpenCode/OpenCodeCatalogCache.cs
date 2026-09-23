using System.Collections.Concurrent;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode;

/// <summary>
/// Keeps what an OpenCode process says about a folder's setup (its agents, commands and providers). Asking is slow,
/// often over half a second for <c>/provider</c>, and OpenCode answers on the same thread that serves a session's
/// messages, so every session switch that asked again held up the conversation it was opening. The answers change
/// only when the folder's config does.
/// </summary>
/// <remarks>
/// An answer is kept fresh for <see cref="FreshFor"/>. After that the kept answer is still handed back at once and a
/// new one is fetched behind it. Callers asking at the same time share one fetch, and one caller giving up doesn't
/// cancel it for the others. <see cref="Forget"/> drops a folder's answers when its config is reloaded.
/// </remarks>
internal sealed class OpenCodeCatalogCache(TimeProvider time)
{
    public static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<(string Kind, string Directory), Entry> _entries = new();

    public async Task<T> GetAsync<T>(string kind, string directory, Func<CancellationToken, Task<T>> fetch, CancellationToken ct)
        where T : class
    {
        var entry = _entries.GetOrAdd((kind, directory), _ => new Entry());
        Task<object> pending;
        lock (entry)
        {
            if (entry.Value is not null)
            {
                if (entry.Fetching is null && time.GetUtcNow() - entry.FetchedAt >= FreshFor)
                {
                    // Nobody waits on a refresh, so a failed one is only observed; the kept answer stays.
                    entry.Fetching = Start(entry, fetch);
                    entry.Fetching.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
                return (T)entry.Value;
            }

            pending = entry.Fetching ??= Start(entry, fetch);
        }

        return (T)await pending.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Drops every answer kept for <paramref name="directory"/>, so the next request asks OpenCode again.</summary>
    public void Forget(string directory)
    {
        foreach (var key in _entries.Keys)
        {
            if (string.Equals(key.Directory, directory, StringComparison.Ordinal))
                _entries.TryRemove(key, out _);
        }
    }

    // Off the caller's thread: the fetch's own locks then can't run before the caller has stored it.
    private Task<object> Start<T>(Entry entry, Func<CancellationToken, Task<T>> fetch) where T : class
        => Task.Run(() => Fetch(entry, fetch));

    private async Task<object> Fetch<T>(Entry entry, Func<CancellationToken, Task<T>> fetch) where T : class
    {
        try
        {
            // Not the caller's token: other callers wait on this fetch too.
            var value = await fetch(CancellationToken.None).ConfigureAwait(false);
            lock (entry)
            {
                entry.Value = value;
                entry.FetchedAt = time.GetUtcNow();
            }
            return value;
        }
        finally
        {
            lock (entry)
                entry.Fetching = null;
        }
    }

    private sealed class Entry
    {
        public object? Value;
        public DateTimeOffset FetchedAt;
        public Task<object>? Fetching;
    }
}

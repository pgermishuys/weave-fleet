using System.Collections.Concurrent;

namespace NuCode.Lsp;

/// <summary>
/// Watches a workspace directory for file system changes and emits debounced batches of
/// <see cref="LspFileChange"/> events. Only changes matching at least one registered glob
/// pattern are emitted; if no patterns are registered all changes are emitted.
/// </summary>
internal sealed class FileWatcherService : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly ConcurrentDictionary<string, (int ChangeType, System.Threading.Timer Timer)> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _debounce = TimeSpan.FromMilliseconds(300);
    private volatile IReadOnlyList<string> _patterns = [];
    private bool _disposed;

    /// <summary>
    /// Raised with a batch of debounced file changes. Invoked on a thread-pool thread.
    /// </summary>
    public event Action<IReadOnlyList<LspFileChange>>? OnChanges;

    public FileWatcherService(string workspaceDirectory)
    {
        _watcher = new FileSystemWatcher(workspaceDirectory)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
            EnableRaisingEvents = true,
        };

        _watcher.Created  += (_, e) => Enqueue(e.FullPath, 1);
        _watcher.Changed  += (_, e) => Enqueue(e.FullPath, 2);
        _watcher.Deleted  += (_, e) => Enqueue(e.FullPath, 3);
        _watcher.Renamed  += (_, e) =>
        {
            Enqueue(e.OldFullPath, 3);
            Enqueue(e.FullPath, 1);
        };
    }

    /// <summary>Updates the set of glob patterns used to filter events.</summary>
    public void SetPatterns(IReadOnlyList<string> patterns)
    {
        _patterns = patterns;
    }

    private void Enqueue(string filePath, int changeType)
    {
        if (!MatchesPatterns(filePath)) return;

        _pending.AddOrUpdate(filePath,
            _ =>
            {
                var timer = new System.Threading.Timer(Fire, filePath, _debounce, Timeout.InfiniteTimeSpan);
                return (changeType, timer);
            },
            (_, existing) =>
            {
                // Reset the debounce timer; keep the most recent change type
                existing.Timer.Change(_debounce, Timeout.InfiniteTimeSpan);
                return (changeType, existing.Timer);
            });
    }

    private void Fire(object? state)
    {
        if (_disposed) return;
        var filePath = (string)state!;
        if (!_pending.TryRemove(filePath, out var entry)) return;

        entry.Timer.Dispose();
        OnChanges?.Invoke([new LspFileChange { FilePath = filePath, ChangeType = entry.ChangeType }]);
    }

    private bool MatchesPatterns(string filePath)
    {
        var patterns = _patterns;
        if (patterns.Count == 0) return true;

        foreach (var pattern in patterns)
        {
            if (MatchesGlob(filePath, pattern)) return true;
        }
        return false;
    }

    /// <summary>Minimal glob match: supports * (any segment chars) and ** (any path).</summary>
    private static bool MatchesGlob(string filePath, string pattern)
    {
        // Normalise separators
        filePath = filePath.Replace('\\', '/');
        pattern  = pattern.Replace('\\', '/');

        return GlobMatch(filePath, pattern);
    }

    private static bool GlobMatch(string text, string pattern)
    {
        // Convert glob to a simple state-machine check
        var ti = 0;
        var pi = 0;
        var starPos = -1;
        var matchPos = 0;

        while (ti < text.Length)
        {
            if (pi < pattern.Length && (pattern[pi] == '?' || pattern[pi] == text[ti]))
            {
                ti++;
                pi++;
            }
            else if (pi < pattern.Length && pattern[pi] == '*')
            {
                if (pi + 1 < pattern.Length && pattern[pi + 1] == '*')
                {
                    // ** matches everything including /
                    starPos = pi;
                    matchPos = ti;
                    pi += 2;
                    if (pi < pattern.Length && pattern[pi] == '/') pi++;
                }
                else
                {
                    // * matches anything except /
                    starPos = pi;
                    matchPos = ti;
                    pi++;
                }
            }
            else if (starPos >= 0)
            {
                pi = starPos + 1;
                matchPos++;
                ti = matchPos;
            }
            else
            {
                return false;
            }
        }

        while (pi < pattern.Length && (pattern[pi] == '*' || pattern[pi] == '/')) pi++;
        return pi == pattern.Length;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        foreach (var (_, (_, timer)) in _pending)
        {
            timer.Dispose();
        }
        _pending.Clear();
    }
}

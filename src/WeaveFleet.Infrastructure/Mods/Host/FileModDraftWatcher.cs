using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Mods;
using WeaveFleet.Application.Mods.Host;

namespace WeaveFleet.Infrastructure.Mods.Host;

/// <summary>
/// Watches <c>drafts/{sessionId}/{name}/</c> and reports each draft once it has been quiet for the debounce.
/// <para>
/// It never follows a link, so it doesn't use <c>IncludeSubdirectories</c>: .NET's recursive watch on Linux adds an inotify
/// watch for a linked folder as well, which would report writes in a folder outside the drafts. Instead it keeps one
/// non-recursive watcher per real folder (the root, each session, each draft and every folder inside a draft) and adds
/// and removes them as folders come and go.
/// </para>
/// <para>
/// A root that doesn't exist yet is waited for: a watcher on its nearest existing ancestor, checked again on every event
/// there and on a slow timer, starts the real watch once the root appears (and reports the drafts it finds, which are
/// new). If the watcher's buffer overflows or it fails, it is rebuilt and every known draft is reported once.
/// </para>
/// </summary>
public sealed partial class FileModDraftWatcher(TimeProvider time, ILogger<FileModDraftWatcher> logger, TimeSpan? debounce = null) : IModDraftWatcher
{
    private static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(300);

    /// <summary>How often a watcher re-checks that its root and the folder above it are still where it left them.</summary>
    private static readonly TimeSpan Recheck = TimeSpan.FromSeconds(5);

    /// <summary>Folders deeper than this inside a draft aren't watched.</summary>
    private const int MaxDepth = 32;

    /// <summary>A watcher per folder; past this many a draft is only partly watched.</summary>
    private const int MaxWatchers = 4096;

    public IDisposable Watch(string draftsRoot, Action<ModDraftChange> changed)
    {
        ArgumentException.ThrowIfNullOrEmpty(draftsRoot);
        ArgumentNullException.ThrowIfNull(changed);
        var subscription = new Subscription(Path.GetFullPath(draftsRoot), changed, time, logger, debounce ?? DefaultDebounce);
        subscription.Start();
        return subscription;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Watching the drafts in {Root} failed ({Reason}); rescanning every draft")]
    private static partial void LogRescan(ILogger logger, string root, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't watch {Folder} for draft changes: {Reason}")]
    private static partial void LogCouldNotWatch(ILogger logger, string folder, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A draft change callback for {SessionId}/{Name} threw")]
    private static partial void LogCallbackFailed(ILogger logger, Exception exception, string sessionId, string name);

    private static bool IsLink(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget is not null || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsRealFolder(string path) => Directory.Exists(path) && !IsLink(path);

    private sealed class Debounced
    {
        public required ITimer Timer { get; init; }
        public bool Armed { get; set; }
        public bool Running { get; set; }
        public bool Pending { get; set; }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly string _root;
        private readonly Action<ModDraftChange> _changed;
        private readonly TimeProvider _time;
        private readonly ILogger _logger;
        private readonly TimeSpan _debounce;
        private readonly object _gate = new();
        private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.Ordinal);
        private readonly HashSet<(string SessionId, string Name)> _known = [];
        private readonly Dictionary<(string SessionId, string Name), Debounced> _pending = [];
        private FileSystemWatcher? _guard;
        private string? _guardFolder;
        private ITimer? _poll;
        private bool _rootWatched;
        private bool _disposed;
        private bool _loggedRescan;
        private bool _loggedLimit;

        public Subscription(string root, Action<ModDraftChange> changed, TimeProvider time, ILogger logger, TimeSpan debounce)
        {
            _root = root.TrimEnd(Path.DirectorySeparatorChar);
            _changed = changed;
            _time = time;
            _logger = logger;
            _debounce = debounce;
        }

        public void Start()
        {
            lock (_gate)
            {
                if (IsRealFolder(_root))
                {
                    _rootWatched = true;
                    Scan(_root, 0, fire: false);
                }

                PointGuard();
                _poll = _time.CreateTimer(_ => Check(), null, Recheck, Recheck);
            }

            // The root may have appeared between the first look and the guard going up.
            Check();
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                _poll?.Dispose();
                _guard?.Dispose();
                foreach (var watcher in _watchers.Values)
                    watcher.Dispose();
                _watchers.Clear();
                foreach (var entry in _pending.Values)
                    entry.Timer.Dispose();
                _pending.Clear();
                _known.Clear();
            }
        }

        // The root, and the folder above it

        /// <summary>Starts the real watch when the root has appeared, stops it when it has gone, and keeps the guard on the nearest folder above the root that exists.</summary>
        private void Check()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;

                var exists = IsRealFolder(_root);
                if (exists && !_rootWatched)
                {
                    _rootWatched = true;
                    Scan(_root, 0, fire: true);
                }
                else if (!exists && _rootWatched)
                {
                    _rootWatched = false;
                    DropWatchers();
                    foreach (var draft in _known)
                        Touch(draft.SessionId, draft.Name);
                    _known.Clear();
                }

                PointGuard();
            }
        }

        private void PointGuard()
        {
            var folder = Path.GetDirectoryName(_root);
            while (folder is { Length: > 0 } && !Directory.Exists(folder))
                folder = Path.GetDirectoryName(folder);
            if (folder == _guardFolder && _guard is not null)
                return;

            _guard?.Dispose();
            _guard = null;
            _guardFolder = folder;
            if (folder is { Length: > 0 })
                _guard = CreateWatcher(folder, onEvent: Check);
        }

        // Watchers

        private FileSystemWatcher? CreateWatcher(string folder, Action? onEvent = null)
        {
            try
            {
                var watcher = new FileSystemWatcher(folder)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                };
                if (onEvent is null)
                {
                    watcher.Created += (_, e) => Handle(e.FullPath);
                    watcher.Changed += (_, e) => Handle(e.FullPath);
                    watcher.Deleted += (_, e) => Handle(e.FullPath);
                    watcher.Renamed += (_, e) =>
                    {
                        Handle(e.OldFullPath);
                        Handle(e.FullPath);
                    };
                }
                else
                {
                    watcher.Created += (_, _) => onEvent();
                    watcher.Deleted += (_, _) => onEvent();
                    watcher.Renamed += (_, _) => onEvent();
                }

                watcher.Error += (_, e) => Rescan(e.GetException().Message);
                watcher.EnableRaisingEvents = true;
                return watcher;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or PlatformNotSupportedException)
            {
                if (Directory.Exists(folder))
                    LogCouldNotWatch(_logger, folder, e.Message);
                return null;
            }
        }

        private void AddWatcher(string folder)
        {
            if (_watchers.ContainsKey(folder))
                return;
            if (_watchers.Count >= MaxWatchers)
            {
                if (!_loggedLimit)
                {
                    _loggedLimit = true;
                    LogCouldNotWatch(_logger, folder, "too many folders are being watched");
                }

                return;
            }

            if (CreateWatcher(folder) is { } watcher)
                _watchers[folder] = watcher;
        }

        private void DropWatchers()
        {
            foreach (var watcher in _watchers.Values)
                watcher.Dispose();
            _watchers.Clear();
        }

        private void DropWatchersUnder(string folder)
        {
            var prefix = folder + Path.DirectorySeparatorChar;
            foreach (var path in _watchers.Keys.Where(p => p == folder || p.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            {
                _watchers[path].Dispose();
                _watchers.Remove(path);
            }
        }

        /// <summary>
        /// Puts a watcher on <paramref name="folder"/> and on every real folder under it that a draft's tree can hold:
        /// the root's children are session folders, a session's are drafts, and a draft's are anything.
        /// </summary>
        private void Scan(string folder, int depth, bool fire)
        {
            if (depth > 2 + MaxDepth)
                return;
            AddWatcher(folder);

            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(folder).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return;
            }

            foreach (var child in children)
            {
                var childName = Path.GetFileName(child);
                if (IsLink(child) || (depth == 0 && !ModNames.IsValidSessionId(childName)) || (depth == 1 && !ModNames.IsValid(childName)))
                    continue;

                if (depth == 1)
                {
                    var sessionId = Path.GetFileName(folder);
                    _known.Add((sessionId, childName));
                    if (fire)
                        Touch(sessionId, childName);
                }

                Scan(child, depth + 1, fire);
            }
        }

        // Events

        private void Handle(string path)
        {
            lock (_gate)
            {
                if (_disposed)
                    return;

                var relative = Path.GetRelativePath(_root, path);
                if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                    return;

                var segments = relative.Split(Path.DirectorySeparatorChar);
                if (!ModNames.IsValidSessionId(segments[0]))
                    return;
                if (segments.Length == 1)
                {
                    ReconcileSession(segments[0]);
                    return;
                }

                if (!ModNames.IsValid(segments[1]))
                    return;
                if (segments.Length == 2)
                {
                    ReconcileDraft(segments[0], segments[1]);
                    return;
                }

                // Something inside a draft: a folder made or moved in needs watching, and anything gone needs its watchers dropped.
                if (IsRealFolder(path))
                    Scan(path, segments.Length, fire: false);
                else
                    DropWatchersUnder(path);
                _known.Add((segments[0], segments[1]));
                Touch(segments[0], segments[1]);
            }
        }

        private void ReconcileSession(string sessionId)
        {
            var folder = Path.Combine(_root, sessionId);
            var gone = _known.Where(d => d.SessionId == sessionId).ToList();
            if (!IsRealFolder(folder))
            {
                DropWatchersUnder(folder);
                foreach (var draft in gone)
                {
                    _known.Remove(draft);
                    Touch(draft.SessionId, draft.Name);
                }

                return;
            }

            AddWatcher(folder);
            foreach (var draft in gone.Where(d => !IsRealFolder(Path.Combine(folder, d.Name))))
                ReconcileDraft(draft.SessionId, draft.Name);

            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(folder).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return;
            }

            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (ModNames.IsValid(name) && !_known.Contains((sessionId, name)) && IsRealFolder(child))
                    ReconcileDraft(sessionId, name);
            }
        }

        private void ReconcileDraft(string sessionId, string name)
        {
            var folder = Path.Combine(_root, sessionId, name);
            if (IsRealFolder(folder) && IsRealFolder(Path.Combine(_root, sessionId)))
            {
                Scan(folder, 2, fire: false);
                _known.Add((sessionId, name));
            }
            else
            {
                DropWatchersUnder(folder);
                _known.Remove((sessionId, name));
            }

            Touch(sessionId, name);
        }

        /// <summary>The watcher failed or its buffer overflowed: rebuild every watcher and report every draft, known before or found now.</summary>
        private void Rescan(string reason)
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                if (!_loggedRescan)
                {
                    _loggedRescan = true;
                    LogRescan(_logger, _root, reason);
                }

                var before = _known.ToList();
                DropWatchers();
                _known.Clear();
                _rootWatched = IsRealFolder(_root);
                if (_rootWatched)
                    Scan(_root, 0, fire: false);
                foreach (var draft in before.Concat(_known).Distinct())
                    Touch(draft.SessionId, draft.Name);
                PointGuard();
            }
        }

        // Debounce

        /// <summary>Called with the gate held.</summary>
        private void Touch(string sessionId, string name)
        {
            var key = (sessionId, name);
            if (!_pending.TryGetValue(key, out var entry))
            {
                entry = new Debounced { Timer = _time.CreateTimer(_ => Fire(key), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan) };
                _pending[key] = entry;
            }

            entry.Armed = true;
            entry.Timer.Change(_debounce, Timeout.InfiniteTimeSpan);
        }

        private void Fire((string SessionId, string Name) key)
        {
            lock (_gate)
            {
                if (_disposed || !_pending.TryGetValue(key, out var entry))
                    return;
                if (entry.Running)
                {
                    // Still inside the last call for this draft: go again once it returns.
                    entry.Pending = true;
                    return;
                }

                entry.Running = true;
                entry.Armed = false;
            }

            try
            {
                _changed(new ModDraftChange(key.SessionId, key.Name));
            }
            catch (Exception e)
            {
                LogCallbackFailed(_logger, e, key.SessionId, key.Name);
            }
            finally
            {
                lock (_gate)
                {
                    if (!_disposed && _pending.TryGetValue(key, out var entry))
                    {
                        entry.Running = false;
                        if (entry.Pending)
                        {
                            entry.Pending = false;
                            entry.Armed = true;
                            entry.Timer.Change(_debounce, Timeout.InfiniteTimeSpan);
                        }
                        else if (!entry.Armed)
                        {
                            entry.Timer.Dispose();
                            _pending.Remove(key);
                        }
                    }
                }
            }
        }
    }
}

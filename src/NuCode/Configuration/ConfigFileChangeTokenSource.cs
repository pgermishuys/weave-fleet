using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace NuCode.Configuration;

/// <summary>
/// Watches project and global config files for changes and signals <see cref="IOptionsMonitor{NuCodeConfig}"/>
/// to reload. Uses <see cref="FileSystemWatcher"/> internally.
/// </summary>
internal sealed class ConfigFileChangeTokenSource : IOptionsChangeTokenSource<NuCodeConfig>, IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = [];
    private CancellationTokenSource _cts = new();

    public string Name => Options.DefaultName;

    internal ConfigFileChangeTokenSource(string workingDirectory)
    {
        WatchFile(ConfigLoader.GetGlobalConfigPath());

        var projectPath = ConfigLoader.FindProjectConfigPath(workingDirectory);
        if (projectPath is not null)
        {
            WatchFile(projectPath);
        }
        else
        {
            // Watch for creation of project config files
            WatchDirectory(workingDirectory, "nucode.jsonc");

            var dotNuCodeDir = Path.Combine(workingDirectory, ".nucode");
            if (Directory.Exists(dotNuCodeDir))
            {
                WatchDirectory(dotNuCodeDir, "config.jsonc");
            }
        }
    }

    public IChangeToken GetChangeToken()
    {
        return new CancellationChangeToken(_cts.Token);
    }

    private void WatchFile(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        var fileName = Path.GetFileName(filePath);

        if (directory is null || !Directory.Exists(directory))
        {
            return;
        }

        var watcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };

        watcher.Changed += OnFileChanged;
        watcher.Created += OnFileChanged;
        watcher.Deleted += OnFileChanged;
        watcher.Renamed += OnFileRenamed;

        _watchers.Add(watcher);
    }

    private void WatchDirectory(string directory, string filter)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        var watcher = new FileSystemWatcher(directory, filter)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };

        watcher.Changed += OnFileChanged;
        watcher.Created += OnFileChanged;
        watcher.Deleted += OnFileChanged;
        watcher.Renamed += OnFileRenamed;

        _watchers.Add(watcher);
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        SignalChange();
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        SignalChange();
    }

    private void SignalChange()
    {
        var previous = Interlocked.Exchange(ref _cts, new CancellationTokenSource());
        previous.Cancel();
        previous.Dispose();
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _watchers.Clear();
        _cts.Dispose();
    }
}

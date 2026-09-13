using WeaveFleet.Application.Browser;

namespace WeaveFleet.Application.Tests.Browser;

/// <summary>Runs nothing: each start is ready at <see cref="NextUrl"/>, or fails with <see cref="NextProblem"/>.</summary>
internal sealed class FakeAppRunner : IAppRunner
{
    private readonly Dictionary<string, AppRunSnapshot> _runs = [];
    private readonly Dictionary<string, List<string>> _logs = [];

    public string? NextUrl { get; set; } = "http://localhost:5173/";
    public string? NextProblem { get; set; }
    public List<string> NextLogs { get; } = [];
    public List<(string SessionId, string Directory, string Command)> Started { get; } = [];
    public List<string> Restarted { get; } = [];

    public AppRunSnapshot Start(string sessionId, string directory, string command)
    {
        Started.Add((sessionId, directory, command));
        var run = new AppRunSnapshot($"app_{_runs.Count + 1}", sessionId, command, directory, AppRunStatus.Starting, null, null, [], [], DateTimeOffset.UtcNow);
        _runs[run.Id] = run;
        _logs[run.Id] = [.. NextLogs];
        return run;
    }

    public AppRunSnapshot? Find(string appId) => _runs.GetValueOrDefault(appId);

    public AppRunSnapshot? FindActive(string sessionId, string command)
        => _runs.Values.LastOrDefault(run => run.SessionId == sessionId && run.Command == command && run.Status != AppRunStatus.Exited);

    public Task<AppReadiness> WaitUntilReadyAsync(string appId, TimeSpan timeout, CancellationToken ct = default)
    {
        var run = _runs[appId];
        if (NextProblem is not null)
        {
            _runs[appId] = run with { Status = AppRunStatus.Exited, ExitCode = 1 };
            return Task.FromResult(new AppReadiness(null, NextProblem));
        }

        _runs[appId] = run with { Status = AppRunStatus.Running, Url = NextUrl, Ports = [new Uri(NextUrl!).Port] };
        return Task.FromResult(new AppReadiness(NextUrl, null));
    }

    public IReadOnlyList<string> Logs(string appId, int maxLines)
        => _logs.TryGetValue(appId, out var lines) ? lines.TakeLast(maxLines).ToList() : [];

    public Task<bool> StopAsync(string appId)
    {
        if (!_runs.TryGetValue(appId, out var run))
            return Task.FromResult(false);
        _runs[appId] = run with { Status = AppRunStatus.Exited };
        return Task.FromResult(true);
    }

    public Task<AppRunSnapshot?> RestartAsync(string appId)
    {
        Restarted.Add(appId);
        if (!_runs.TryGetValue(appId, out var run))
            return Task.FromResult<AppRunSnapshot?>(null);
        _runs[appId] = run with { Status = AppRunStatus.Starting, Url = null };
        return Task.FromResult<AppRunSnapshot?>(_runs[appId]);
    }
}

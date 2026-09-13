using WeaveFleet.Application.Browser;

namespace WeaveFleet.Application.Tests.Browser;

/// <summary>
/// Runs nothing: each start is ready at <see cref="NextUrl"/>, or fails with <see cref="NextProblem"/>, and
/// <see cref="NextRefusal"/> refuses the next start as a cap would.
/// </summary>
internal sealed class FakeAppRunner : IAppRunner
{
    private readonly Dictionary<string, AppRunSnapshot> _runs = [];
    private readonly Dictionary<string, List<string>> _logs = [];

    public event Action<AppRunChange>? Changed;

    public string? NextUrl { get; set; } = "http://localhost:5173/";
    public string? NextProblem { get; set; }
    public string? NextRefusal { get; set; }
    public List<string> NextLogs { get; } = [];
    /// <summary>Ports a ready app listens on besides the page's.</summary>
    public List<int> NextExtraPorts { get; } = [];
    public List<AppRunRequest> Started { get; } = [];
    public List<string> Restarted { get; } = [];
    public List<string> Stopped { get; } = [];
    public List<(int Pid, DateTimeOffset StartedAt)> Leftovers { get; } = [];
    public List<(int Pid, DateTimeOffset StartedAt)> Killed { get; } = [];

    /// <summary>Runs while <see cref="WaitUntilReadyAsync"/> waits, before the page answers.</summary>
    public Func<Task>? WhileWaiting { get; set; }

    public Task<AppStartOutcome> StartAsync(AppRunRequest request)
    {
        if (NextRefusal is { } refusal)
        {
            NextRefusal = null;
            return Task.FromResult(new AppStartOutcome(null, refusal));
        }

        Started.Add(request);
        var run = new AppRunSnapshot(
            request.Id, request.SessionId, request.UserId, request.Command, request.Directory, request.Port ?? 40000 + _runs.Count,
            AppRunStatus.Starting, null, null, [], [], [], Pid: 1000 + _runs.Count, PidStartedAt: DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        _runs[run.Id] = run;
        _logs[run.Id] = [.. NextLogs];
        Changed?.Invoke(new AppRunChange(run, AppChangeReason.Started));
        return Task.FromResult(new AppStartOutcome(run, null));
    }

    public void Seed(AppRunSnapshot run) => _runs[run.Id] = run;

    public AppRunSnapshot? Find(string appId) => _runs.GetValueOrDefault(appId);

    public AppRunSnapshot? FindActive(string sessionId, string command)
        => _runs.Values.LastOrDefault(run => run.SessionId == sessionId && run.Command == command && run.IsLive);

    public async Task<AppReadiness> WaitUntilReadyAsync(string appId, TimeSpan timeout, CancellationToken ct = default)
    {
        if (WhileWaiting is { } whileWaiting)
            await whileWaiting();

        var run = _runs[appId];
        if (NextProblem is not null)
        {
            _runs[appId] = run with { Status = AppRunStatus.Exited, ExitCode = 1 };
            return new AppReadiness(null, NextProblem);
        }

        _runs[appId] = run with { Status = AppRunStatus.Running, Url = NextUrl, Ports = [new Uri(NextUrl!).Port, .. NextExtraPorts] };
        return new AppReadiness(NextUrl, null);
    }

    public IReadOnlyList<string> Logs(string appId, int maxLines)
        => _logs.TryGetValue(appId, out var lines) ? lines.TakeLast(maxLines).ToList() : [];

    public AppOutput Output(string appId, long after)
    {
        var lines = _logs.GetValueOrDefault(appId) ?? [];
        return new AppOutput([.. lines.Skip((int)Math.Min(after, lines.Count))], lines.Count);
    }

    public Task<bool> StopAsync(string appId)
    {
        if (!_runs.TryGetValue(appId, out var run))
            return Task.FromResult(false);
        Stopped.Add(appId);
        _runs[appId] = run with { Status = AppRunStatus.Stopped };
        return Task.FromResult(true);
    }

    public Task<AppStartOutcome> RestartAsync(string appId)
    {
        Restarted.Add(appId);
        if (!_runs.TryGetValue(appId, out var run))
            return Task.FromResult(new AppStartOutcome(null, $"No app {appId}."));
        _runs[appId] = run with { Status = AppRunStatus.Starting, Url = null };
        return Task.FromResult(new AppStartOutcome(_runs[appId], null));
    }

    public bool KillLeftover(int pid, DateTimeOffset startedAt)
    {
        if (!Leftovers.Contains((pid, startedAt)))
            return false;
        Killed.Add((pid, startedAt));
        return true;
    }
}

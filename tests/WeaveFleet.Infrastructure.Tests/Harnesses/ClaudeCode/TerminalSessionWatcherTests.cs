using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using WeaveFleet.Application.TerminalSessions;
using WeaveFleet.Infrastructure.Harnesses.ClaudeCode;
using Terminal = WeaveFleet.Application.TerminalSessions.TerminalSessions;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.ClaudeCode;

/// <summary>An open terminal session's transcript is watched, so a turn written to it is read at once, not at the next heartbeat.</summary>
public sealed class TerminalSessionWatcherTests : IDisposable
{
    private const string ClaudeSessionId = "11111111-2222-4333-8444-555555555555";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"fleet-watch-{Guid.NewGuid():N}");
    private readonly Recorder _recorder = new();
    private readonly TerminalSessionPresence _presence = new(new FakeTimeProvider());
    private readonly TerminalSessionWatcher _watcher;

    public TerminalSessionWatcherTests()
    {
        Directory.CreateDirectory(_folder);
        var services = new ServiceCollection().AddSingleton<ITerminalSessions>(_recorder).BuildServiceProvider();
        _watcher = new TerminalSessionWatcher(_presence, services.GetRequiredService<IServiceScopeFactory>(), NullLogger<TerminalSessionWatcher>.Instance);
    }

    public void Dispose()
    {
        _watcher.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public async Task A_turn_written_to_the_transcript_of_an_open_session_is_read_at_once_and_a_closed_session_is_not_watched()
    {
        await _watcher.StartAsync(CancellationToken.None);
        var transcript = Path.Combine(_folder, ClaudeSessionId + ".jsonl");

        // The MCP server starts before anything is typed: the file isn't there yet.
        _presence.Touch(ClaudeSessionId, "/work/orchard", transcript, "user-1");
        await File.WriteAllTextAsync(transcript, "{}\n");

        var report = await _recorder.Next.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(Terminal.Changed, report.Event);
        Assert.Equal(ClaudeSessionId, report.SessionId);

        _presence.Remove(ClaudeSessionId);
        _recorder.Reset();
        await File.AppendAllTextAsync(transcript, "{}\n");
        await Task.Delay(800);
        Assert.False(_recorder.Next.Task.IsCompleted);

        await _watcher.StopAsync(CancellationToken.None);
    }

    private sealed class Recorder : ITerminalSessions
    {
        public TaskCompletionSource<TerminalSessionReport> Next { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Reset() => Next = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<TerminalSessionAnswer> ReportAsync(TerminalSessionReport report, CancellationToken ct)
        {
            Next.TrySetResult(report);
            return Task.FromResult(new TerminalSessionAnswer("present", null));
        }
    }
}

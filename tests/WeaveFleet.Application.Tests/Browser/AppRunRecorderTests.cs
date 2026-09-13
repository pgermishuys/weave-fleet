using WeaveFleet.Application.Browser;
using WeaveFleet.Application.Tests.Canvases;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Browser;

public sealed class AppRunRecorderTests
{
    private const string SessionId = "ses-1";
    private const string Owner = "owner-user";

    private readonly InMemoryAppRunRepository _runs = new();
    private readonly FakeEventBroadcaster _events = new();
    private readonly ScopedUser _user = new();
    private readonly FakeAppRunner _apps = new();
    private readonly AppRunRecorder _recorder;

    public AppRunRecorderTests()
    {
        _runs.AddSession(SessionId);
        _recorder = new AppRunRecorder(_apps, _runs, _events, _user);
    }

    [Fact]
    public async Task A_change_is_stored_and_sent_to_the_session_as_the_run_is_now()
    {
        var started = (await _apps.StartAsync(new AppRunRequest("app_1", SessionId, Owner, "/work/shop", "bun run dev", 41234))).App!;
        await _apps.WaitUntilReadyAsync("app_1", TimeSpan.FromSeconds(1));

        // The "started" change arrives after the app is already running: what's stored is the run as it is now.
        await _recorder.RecordAsync(new AppRunChange(started, AppChangeReason.Started));

        var stored = _runs.All.ShouldHaveSingleItem();
        (stored.UserId, stored.Command, stored.Port, stored.Status, stored.Url).ShouldBe((Owner, "bun run dev", 41234, "running", "http://localhost:5173/"));
        stored.Pid.ShouldBe(started.Pid);
        stored.PidStartedAt.ShouldNotBeNull();

        var sent = _events.Broadcasts.ShouldHaveSingleItem();
        (sent.Topic, sent.Type, sent.UserId).ShouldBe(($"session:{SessionId}", "app.updated", Owner));
        var payload = sent.DomainEvent.ShouldBeOfType<AppUpdated>().Payload;
        (payload.AppId, payload.Status, payload.Reason, payload.Url).ShouldBe(("app_1", "running", "started", "http://localhost:5173/"));
        payload.Ports.ShouldBe([5173]);
        sent.Payload.GetProperty("status").GetString().ShouldBe("running");
    }

    [Fact]
    public async Task A_run_that_serves_a_page_becomes_its_projects_preview_command()
    {
        var started = (await _apps.StartAsync(new AppRunRequest("app_1", SessionId, Owner, "/work/shop", "bun run dev"))).App!;
        await _recorder.RecordAsync(new AppRunChange(started, AppChangeReason.Started));
        (await _runs.GetPreviewCommandAsync(SessionId)).ShouldBeNull();

        await _apps.WaitUntilReadyAsync("app_1", TimeSpan.FromSeconds(1));
        await _recorder.RecordAsync(new AppRunChange(_apps.Find("app_1")!, AppChangeReason.Ready));

        (await _runs.GetPreviewCommandAsync(SessionId)).ShouldBe("bun run dev");
    }

    [Fact]
    public async Task A_run_keeps_its_creation_time_across_changes()
    {
        var started = (await _apps.StartAsync(new AppRunRequest("app_1", SessionId, Owner, "/work/shop", "bun run dev"))).App!;
        await _recorder.RecordAsync(new AppRunChange(started, AppChangeReason.Started));
        var created = _runs.All.ShouldHaveSingleItem().CreatedAt;

        await _apps.StopAsync("app_1");
        await _recorder.RecordAsync(new AppRunChange(_apps.Find("app_1")!, AppChangeReason.Stopped));

        var stored = _runs.All.ShouldHaveSingleItem();
        (stored.Status, stored.CreatedAt).ShouldBe(("stopped", created));
    }

    [Fact]
    public async Task Nothing_is_sent_for_a_session_that_is_gone()
    {
        var started = (await _apps.StartAsync(new AppRunRequest("app_1", "ses-deleted", Owner, "/work/shop", "bun run dev"))).App!;

        await _recorder.RecordAsync(new AppRunChange(started, AppChangeReason.Stopped));

        _runs.All.ShouldBeEmpty();
        _events.Broadcasts.ShouldBeEmpty();
    }

    [Fact]
    public async Task At_startup_processes_a_crashed_Fleet_left_are_killed_and_its_runs_marked_stopped()
    {
        var startedAt = new DateTimeOffset(2026, 9, 13, 8, 0, 0, TimeSpan.Zero);
        await StoreAsync("app_left", "running", pid: 4242, startedAt);
        await StoreAsync("app_reused", "starting", pid: 4343, startedAt);
        await StoreAsync("app_done", "exited", pid: null, startedAt: null);
        _apps.Leftovers.Add((4242, startedAt));

        var killed = await _recorder.CleanUpAfterPreviousFleetAsync();

        killed.ShouldBe(1);
        _apps.Killed.ShouldBe([(4242, startedAt)]);
        var runs = _runs.All.ToDictionary(run => run.Id);
        (runs["app_left"].Status, runs["app_left"].Pid).ShouldBe(("stopped", (int?)null));
        runs["app_reused"].Status.ShouldBe("stopped");
        runs["app_done"].Status.ShouldBe("exited");
    }

    private Task<bool> StoreAsync(string id, string status, int? pid, DateTimeOffset? startedAt)
        => _runs.UpsertAsync(new AppRun
        {
            Id = id, SessionId = SessionId, UserId = Owner, Command = "bun run dev", Directory = "/work/shop", Port = 41000,
            Status = status, Pid = pid, PidStartedAt = startedAt?.ToString("O"), CreatedAt = "2026-09-13T08:00:00Z", UpdatedAt = "2026-09-13T08:00:00Z",
        });
}

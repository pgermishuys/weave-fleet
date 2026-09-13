using WeaveFleet.Application.Browser;
using WeaveFleet.Application.Tests.Canvases;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Browser;

public sealed class AppRunServiceTests : IDisposable
{
    private const string SessionId = "ses-1";

    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("fleet-app-runs-");
    private readonly InMemorySessionRepository _sessions = new();
    private readonly InMemoryAppRunRepository _runs = new();
    private readonly ScopedUser _user = new();
    private readonly FakeAppRunner _apps = new();
    private readonly AppRunService _service;

    public AppRunServiceTests()
    {
        _runs.AddSession(SessionId);
        _sessions.Seed(new Session { Id = SessionId, Directory = _folder.FullName });
        _service = new AppRunService(_apps, _runs, _sessions, _user);
    }

    public void Dispose() => _folder.Delete(recursive: true);

    [Fact]
    public async Task A_new_command_gets_a_new_run_owned_by_the_current_user()
    {
        var started = await _service.StartAsync(SessionId, "npm run dev");

        started.Restarted.ShouldBeFalse();
        var request = _apps.Started.ShouldHaveSingleItem();
        request.UserId.ShouldBe(ScopedUser.RequestUser);
        request.Directory.ShouldBe(_folder.FullName);
        request.Port.ShouldBeNull();
        started.App!.Id.ShouldBe(request.Id);
    }

    [Fact]
    public async Task A_session_without_a_folder_on_this_machine_runs_nothing()
    {
        _sessions.Seed(new Session { Id = "ses-2", Directory = Path.Combine(_folder.FullName, "gone") });

        var started = await _service.StartAsync("ses-2", "npm run dev");

        started.Problem.ShouldBe("This session has no folder on this machine to run the command in.");
        _apps.Started.ShouldBeEmpty();
    }

    [Fact]
    public async Task Restarting_a_stored_run_starts_it_under_its_id_and_port()
    {
        await StoreAsync("app_old", port: 41234, status: "stopped");

        var started = await _service.RestartAsync(SessionId, "app_old");

        started.Restarted.ShouldBeFalse();
        var request = _apps.Started.ShouldHaveSingleItem();
        (request.Id, request.Port, request.Command).ShouldBe(("app_old", 41234, "npm run dev"));
    }

    [Fact]
    public async Task Restarting_a_live_run_restarts_it()
    {
        var app = (await _service.StartAsync(SessionId, "npm run dev")).App!;

        var restarted = await _service.RestartAsync(SessionId, app.Id);

        restarted.Restarted.ShouldBeTrue();
        _apps.Restarted.ShouldBe([app.Id]);
    }

    [Fact]
    public async Task A_stored_run_Fleet_is_not_running_is_stopped_unless_it_had_exited()
    {
        await StoreAsync("app_stopped", port: 41000, status: "stopped");
        await StoreAsync("app_exited", port: 41001, status: "exited", exitCode: 1);

        (await _service.GetAsync(SessionId, "app_stopped"))!.Status.ShouldBe(AppRunStatus.Stopped);
        var exited = (await _service.GetAsync(SessionId, "app_exited"))!;
        (exited.Status, exited.ExitCode).ShouldBe((AppRunStatus.Exited, 1));
    }

    [Fact]
    public async Task Another_users_or_sessions_live_app_is_not_found()
    {
        await _apps.StartAsync(new AppRunRequest("app_theirs", SessionId, "someone-else", _folder.FullName, "npm run dev"));
        await _apps.StartAsync(new AppRunRequest("app_elsewhere", "ses-9", ScopedUser.RequestUser, _folder.FullName, "npm run dev"));

        (await _service.GetAsync(SessionId, "app_theirs")).ShouldBeNull();
        (await _service.GetAsync(SessionId, "app_elsewhere")).ShouldBeNull();
        (await _service.StopAsync(SessionId, "app_theirs")).ShouldBeFalse();
        (await _service.RestartAsync(SessionId, "app_theirs")).IsNotFound.ShouldBeTrue();
        (await _service.OutputAsync(SessionId, "app_theirs", 0)).ShouldBeNull();
        _apps.Stopped.ShouldBeEmpty();
        _apps.Restarted.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_archived_session_runs_no_apps()
    {
        await StoreAsync("app_old", port: 41234, status: "stopped");
        _sessions.Seed(new Session { Id = SessionId, Directory = _folder.FullName, RetentionStatus = "archived" });

        (await _service.StartAsync(SessionId, "npm run dev")).Problem.ShouldBe("This session is archived, so Fleet doesn't run apps for it.");
        (await _service.RestartAsync(SessionId, "app_old")).Problem.ShouldBe("This session is archived, so Fleet doesn't run apps for it.");
        _apps.Started.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_refused_start_says_why()
    {
        _apps.NextRefusal = "Fleet already runs 10 apps across sessions, its limit.";

        var started = await _service.StartAsync(SessionId, "npm run dev");

        started.App.ShouldBeNull();
        started.Problem.ShouldBe("Fleet already runs 10 apps across sessions, its limit.");
    }

    private Task<bool> StoreAsync(string id, int port, string status, int? exitCode = null)
        => _runs.UpsertAsync(new AppRun
        {
            Id = id, SessionId = SessionId, UserId = ScopedUser.RequestUser, Command = "npm run dev", Directory = _folder.FullName,
            Port = port, Status = status, ExitCode = exitCode, CreatedAt = "2026-09-13T08:00:00Z", UpdatedAt = "2026-09-13T08:00:00Z",
        });
}

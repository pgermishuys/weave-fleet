using System.Text.Json.Nodes;
using WeaveFleet.Application.Browser;
using WeaveFleet.Application.Canvases;
using WeaveFleet.Application.Tests.Canvases;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Browser;

public sealed class BrowserBridgeTests : IDisposable
{
    private const string Token = "token-1";
    private const string OpenCodeSessionId = "oc-1";
    private const string SessionId = "ses-1";
    private const string Owner = "owner-user";

    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("fleet-browser-bridge-");
    private readonly InMemoryCanvasRepository _canvasRepository = new();
    private readonly InMemorySessionRepository _sessions = new();
    private readonly InMemoryAppRunRepository _runs = new();
    private readonly ScopedUser _user = new();
    private readonly FakeCallers _callers = new();
    private readonly FakeAppRunner _apps = new();
    private readonly FakeScreenshotter _shots = new();
    private readonly BrowserBridge _bridge;

    public BrowserBridgeTests()
    {
        _canvasRepository.AddSession(SessionId);
        _runs.AddSession(SessionId);
        _sessions.Seed(new Session { Id = SessionId, Directory = _folder.FullName });
        _callers.Add(Token, OpenCodeSessionId, new HarnessCanvasCaller(SessionId, Owner));
        var canvases = new CanvasService(_canvasRepository, new FakeEventBroadcaster(), _user);
        var apps = new AppRunService(_apps, _runs, _sessions, _user);
        _bridge = new BrowserBridge([_callers], _user, new BrowserPreviews(canvases, apps), apps, canvases, _shots);
    }

    public void Dispose() => _folder.Delete(recursive: true);

    [Fact]
    public async Task App_start_runs_the_command_in_the_session_folder_and_shows_the_page_it_found()
    {
        var started = await _bridge.AppStartAsync(Token, OpenCodeSessionId, " bun run dev ", "Shop");

        var request = _apps.Started.ShouldHaveSingleItem();
        request.ShouldBe(request with { SessionId = SessionId, UserId = Owner, Directory = _folder.FullName, Command = "bun run dev", Port = null });
        request.Id.ShouldStartWith("app_");
        var canvas = (await _canvasRepository.ListBySessionIdAsync(SessionId)).ShouldHaveSingleItem();
        canvas.Kind.ShouldBe(CanvasKinds.Browser);
        canvas.UserId.ShouldBe(Owner);
        BrowserState.Parse(canvas.StateJson).Url.ShouldBe("http://localhost:5173/");
        BrowserState.Parse(canvas.StateJson).AppId.ShouldBe(request.Id);
        started.Value!.Title.ShouldBe("Shop · http://localhost:5173/");
        started.Value.Output.ShouldStartWith($"Running `bun run dev` ({request.Id}) in {_folder.FullName}.\nShowing http://localhost:5173/ in \"Shop\" ({canvas.Id}).");
    }

    [Fact]
    public async Task The_tab_opens_as_soon_as_the_app_starts_and_shows_the_page_once_it_answers()
    {
        BrowserState? whileStarting = null;
        _apps.WhileWaiting = async () =>
            whileStarting = BrowserState.Parse((await _canvasRepository.ListBySessionIdAsync(SessionId)).ShouldHaveSingleItem().StateJson);

        await _bridge.AppStartAsync(Token, OpenCodeSessionId, "bun run dev", "Shop");

        whileStarting.ShouldNotBeNull();
        whileStarting.Url.ShouldBe("");
        whileStarting.AppId.ShouldBe(_apps.Started.ShouldHaveSingleItem().Id);
        BrowserState.Parse((await _canvasRepository.ListBySessionIdAsync(SessionId)).ShouldHaveSingleItem().StateJson).Url.ShouldBe("http://localhost:5173/");
    }

    [Fact]
    public async Task Starting_the_same_command_again_restarts_it_and_keeps_the_page_in_the_tab()
    {
        await _bridge.AppStartAsync(Token, OpenCodeSessionId, "bun run dev", "Shop");
        var appId = _apps.Started.ShouldHaveSingleItem().Id;
        string? shownDuringRestart = null;
        _apps.WhileWaiting = async () =>
            shownDuringRestart = BrowserState.Parse((await _canvasRepository.ListBySessionIdAsync(SessionId)).ShouldHaveSingleItem().StateJson).Url;

        var again = await _bridge.AppStartAsync(Token, OpenCodeSessionId, "bun run dev", "Shop");

        _apps.Started.Count.ShouldBe(1);
        _apps.Restarted.ShouldBe([appId]);
        shownDuringRestart.ShouldBe("http://localhost:5173/");
        again.Value!.Output.ShouldStartWith($"Restarted `bun run dev` ({appId})");
        (await _canvasRepository.ListBySessionIdAsync(SessionId)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_command_Fleet_ran_before_it_restarted_starts_again_under_its_id_and_port()
    {
        await _runs.UpsertAsync(new AppRun
        {
            Id = "app_old", SessionId = SessionId, UserId = Owner, Command = "bun run dev", Directory = _folder.FullName,
            Port = 41234, Status = "stopped", CreatedAt = "2026-09-13T08:00:00Z", UpdatedAt = "2026-09-13T08:00:00Z",
        });

        await _bridge.AppStartAsync(Token, OpenCodeSessionId, "bun run dev", "Shop");

        var request = _apps.Started.ShouldHaveSingleItem();
        request.Id.ShouldBe("app_old");
        request.Port.ShouldBe(41234);
    }

    [Fact]
    public async Task A_command_that_serves_nothing_returns_the_problem_and_its_last_output_and_the_tab_stays_on_the_app()
    {
        _apps.NextProblem = "`bun run dev` exited with code 1 before serving a page.";
        _apps.NextLogs.AddRange(["$ bun --hot server.ts", "error: Cannot find module \"./index.html\""]);

        var started = await _bridge.AppStartAsync(Token, OpenCodeSessionId, "bun run dev", "Shop");

        started.Error!.Kind.ShouldBe(CanvasErrorKind.Invalid);
        started.Error.Message.ShouldStartWith(
            "`bun run dev` exited with code 1 before serving a page.\nLast output:\n$ bun --hot server.ts\nerror: Cannot find module \"./index.html\"");
        var tab = BrowserState.Parse((await _canvasRepository.ListBySessionIdAsync(SessionId)).ShouldHaveSingleItem().StateJson);
        tab.AppId.ShouldBe(_apps.Started.ShouldHaveSingleItem().Id);
    }

    [Fact]
    public async Task A_start_past_the_cap_returns_why_and_opens_no_tab()
    {
        _apps.NextRefusal = "This session already runs 3 apps, its limit: `a` (app_1), `b` (app_2), `c` (app_3).";

        var started = await _bridge.AppStartAsync(Token, OpenCodeSessionId, "bun run dev", "Shop");

        started.Error!.Message.ShouldStartWith("This session already runs 3 apps");
        (await _canvasRepository.ListBySessionIdAsync(SessionId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Other_ports_are_told_apart_from_the_page_by_number()
    {
        _apps.NextUrl = "http://localhost:8080/";
        _apps.NextExtraPorts.Add(80);

        var started = await _bridge.AppStartAsync(Token, OpenCodeSessionId, "bun run dev", "Shop");

        started.Value!.Output.ShouldContain("\nIt also listens on 80.");
    }

    [Theory]
    [InlineData("https://example.com/")]
    [InlineData("file:///etc/passwd")]
    [InlineData("localhost:5173")]
    public async Task Browser_open_only_shows_pages_on_this_machine(string url)
    {
        var opened = await _bridge.BrowserOpenAsync(Token, OpenCodeSessionId, url, "Page");

        opened.Error!.Message.ShouldBe(LoopbackUrl.Requirement);
        (await _canvasRepository.ListBySessionIdAsync(SessionId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Browser_open_shows_a_running_page_without_an_app()
    {
        var opened = await _bridge.BrowserOpenAsync(Token, OpenCodeSessionId, "http://0.0.0.0:8080/admin", "Admin");

        var canvas = (await _canvasRepository.ListBySessionIdAsync(SessionId)).ShouldHaveSingleItem();
        BrowserState.Parse(canvas.StateJson).AppId.ShouldBeNull();
        opened.Value!.Output.ShouldBe($"Showing http://0.0.0.0:8080/admin in \"Admin\" ({canvas.Id}).");
    }

    [Fact]
    public async Task Browser_open_needs_an_address_even_though_a_starting_app_tab_has_none()
    {
        var opened = await _bridge.BrowserOpenAsync(Token, OpenCodeSessionId, "", "Page");

        opened.Error!.Message.ShouldBe(LoopbackUrl.Requirement);
    }

    [Fact]
    public async Task A_screenshot_shoots_the_page_the_canvas_shows_and_comes_back_as_an_image()
    {
        var canvasId = await ShownAppAsync();

        var shot = await _bridge.ScreenshotAsync(Token, OpenCodeSessionId, canvasId, "", "desktop");

        _shots.Requests.ShouldHaveSingleItem().ShouldBe(new ScreenshotRequest("http://localhost:5173/", 1280, 800));
        var image = shot.Value!.Attachments.ShouldHaveSingleItem();
        image.Mime.ShouldBe("image/png");
        image.FileName.ShouldBe("screenshot.png");
        image.Content.ShouldBe(_shots.NextPng);
        shot.Value.Title.ShouldBe("Shop · 1280×800");
        shot.Value.CanvasId.ShouldBe(canvasId);
        shot.Value.Output.ShouldStartWith("Screenshot of http://localhost:5173/ at 1280×800, from \"Shop\"");
    }

    [Fact]
    public async Task A_phone_screenshot_uses_the_narrow_window()
    {
        var canvasId = await ShownAppAsync();

        var shot = await _bridge.ScreenshotAsync(Token, OpenCodeSessionId, canvasId, "", "phone");

        _shots.Requests.ShouldHaveSingleItem().ShouldBe(new ScreenshotRequest("http://localhost:5173/", 390, 844));
        shot.Value!.Title.ShouldBe("Shop · 390×844");
    }

    [Fact]
    public async Task A_path_shoots_another_page_of_the_same_app_without_moving_the_canvas()
    {
        var canvasId = await ShownAppAsync();

        await _bridge.ScreenshotAsync(Token, OpenCodeSessionId, canvasId, "/settings?tab=theme", "desktop");

        _shots.Requests.ShouldHaveSingleItem().Url.ShouldBe("http://localhost:5173/settings?tab=theme");
        var canvas = (await _canvasRepository.ListBySessionIdAsync(SessionId)).ShouldHaveSingleItem();
        BrowserState.Parse(canvas.StateJson).Url.ShouldBe("http://localhost:5173/");
    }

    [Theory]
    [InlineData("https://example.com/")]
    [InlineData("//example.com/")]
    public async Task A_path_that_leaves_this_machine_is_refused(string path)
    {
        var canvasId = await ShownAppAsync();

        var shot = await _bridge.ScreenshotAsync(Token, OpenCodeSessionId, canvasId, path, "desktop");

        shot.Error!.Message.ShouldBe(LoopbackUrl.Requirement);
        _shots.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unknown_viewport_says_which_ones_there_are()
    {
        var canvasId = await ShownAppAsync();

        var shot = await _bridge.ScreenshotAsync(Token, OpenCodeSessionId, canvasId, "", "retina");

        shot.Error!.Message.ShouldBe(ScreenshotViewports.Requirement);
        _shots.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_screenshot_of_an_app_that_stopped_says_so_with_its_last_output()
    {
        var canvasId = await ShownAppAsync();
        _apps.NextLogs.Add("error: listen EADDRINUSE");
        await _apps.StopAsync(_apps.Started.ShouldHaveSingleItem().Id);

        var shot = await _bridge.ScreenshotAsync(Token, OpenCodeSessionId, canvasId, "", "desktop");

        shot.Error!.Kind.ShouldBe(CanvasErrorKind.Invalid);
        shot.Error.Message.ShouldStartWith("The app in this canvas isn't running (stopped), so there's no page to shoot. Start it again with fleet_app_start.");
        _shots.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_caveat_about_the_picture_is_told_to_the_agent_with_it()
    {
        var canvasId = await ShownAppAsync();
        _shots.NextNote = "The page hadn't finished loading after 10 seconds; this is how far it had got.";

        var shot = await _bridge.ScreenshotAsync(Token, OpenCodeSessionId, canvasId, "", "desktop");

        shot.Value!.Output.ShouldBe(
            "Screenshot of http://localhost:5173/ at 1280×800, from \"Shop\" (" + canvasId + ")."
            + "\nThe page hadn't finished loading after 10 seconds; this is how far it had got."
            + "\nThe image is attached: look at it, don't guess. Call this again after a change to see it.");
        shot.Value.Attachments.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_screenshot_of_a_diagram_canvas_says_to_open_a_page_first()
    {
        var canvases = new CanvasService(_canvasRepository, new FakeEventBroadcaster(), _user);
        var diagram = await canvases.OpenAsync(SessionId, CanvasKinds.Sequence, "Flow", JsonNode.Parse("{\"source\":\"sequenceDiagram\"}"));

        var shot = await _bridge.ScreenshotAsync(Token, OpenCodeSessionId, diagram.Value!.Canvas.Id, "", "desktop");

        shot.Error!.Message.ShouldBe($"\"Flow\" ({diagram.Value.Canvas.Id}) is a sequence canvas. Screenshots are of pages: use fleet_app_start or fleet_browser_open first.");
    }

    [Fact]
    public async Task A_screenshot_without_a_browser_on_the_machine_says_what_to_install()
    {
        var canvasId = await ShownAppAsync();
        _shots.NextProblem = "No Chrome, Edge or Chromium on this machine, so Fleet can't take screenshots.";

        var shot = await _bridge.ScreenshotAsync(Token, OpenCodeSessionId, canvasId, "", "desktop");

        shot.Error!.Message.ShouldBe(_shots.NextProblem);
    }

    [Fact]
    public async Task A_screenshot_of_a_canvas_in_another_session_is_not_found()
    {
        var shot = await _bridge.ScreenshotAsync(Token, OpenCodeSessionId, "cv_other", "", "desktop");

        shot.Error!.Kind.ShouldBe(CanvasErrorKind.NotFound);
        shot.Error.Message.ShouldBe("No canvas cv_other in this session.");
    }

    /// <summary>A canvas showing a running app, the way fleet_app_start leaves one.</summary>
    private async Task<string> ShownAppAsync()
    {
        var started = await _bridge.AppStartAsync(Token, OpenCodeSessionId, "bun run dev", "Shop");
        return started.Value!.CanvasId!;
    }

    [Fact]
    public async Task A_call_Fleet_cannot_place_gets_the_same_not_found()
    {
        var started = await _bridge.AppStartAsync("token-2", OpenCodeSessionId, "bun run dev", "Shop");

        started.Error.ShouldBe(new CanvasError(CanvasErrorKind.NotFound, CanvasBridge.UnknownCallerMessage));
        _apps.Started.ShouldBeEmpty();
    }
}

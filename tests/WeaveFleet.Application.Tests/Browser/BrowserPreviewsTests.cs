using WeaveFleet.Application.Browser;
using WeaveFleet.Application.Canvases;
using WeaveFleet.Application.Tests.Canvases;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Browser;

public sealed class BrowserPreviewsTests : IDisposable
{
    private const string SessionId = "ses-1";

    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("fleet-browser-previews-");
    private readonly InMemoryCanvasRepository _canvasRepository = new();
    private readonly InMemorySessionRepository _sessions = new();
    private readonly InMemoryAppRunRepository _runs = new();
    private readonly ScopedUser _user = new();
    private readonly FakeAppRunner _apps = new();
    private readonly BrowserPreviews _previews;

    public BrowserPreviewsTests()
    {
        _canvasRepository.AddSession(SessionId);
        _runs.AddSession(SessionId);
        _sessions.Seed(new Session { Id = SessionId, Directory = _folder.FullName });
        var canvases = new CanvasService(_canvasRepository, new FakeEventBroadcaster(), _user);
        _previews = new BrowserPreviews(canvases, new AppRunService(_apps, _runs, _sessions, _user));
    }

    public void Dispose() => _folder.Delete(recursive: true);

    [Fact]
    public async Task A_command_from_the_user_opens_its_tab_at_once_without_waiting_for_the_page()
    {
        var preview = await _previews.StartAppAsync(SessionId, "npm run dev", "npm run dev", restartLive: false);

        var appId = _apps.Started.ShouldHaveSingleItem().Id;
        preview.Value!.Started.App!.Id.ShouldBe(appId);
        preview.Value.Canvas.Title.ShouldBe("npm run dev");
        var state = BrowserState.Parse(preview.Value.Canvas.StateJson);
        (state.Url, state.AppId).ShouldBe(("", appId));
    }

    [Fact]
    public async Task A_command_the_session_already_runs_is_left_running_and_its_tab_comes_forward()
    {
        var first = await _previews.StartAppAsync(SessionId, "npm run dev", "Shop", restartLive: false);

        var again = await _previews.StartAppAsync(SessionId, "npm run dev", "npm run dev", restartLive: false);

        _apps.Restarted.ShouldBeEmpty();
        _apps.Started.ShouldHaveSingleItem();
        again.Value!.Canvas.Id.ShouldBe(first.Value!.Canvas.Id);
        (await _canvasRepository.ListBySessionIdAsync(SessionId)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_running_app_without_a_tab_gets_one_that_shows_its_page()
    {
        await _previews.StartAppAsync(SessionId, "npm run dev", "Shop", restartLive: false);
        var appId = _apps.Started.ShouldHaveSingleItem().Id;
        await _apps.WaitUntilReadyAsync(appId, TimeSpan.FromSeconds(1));
        var shop = (await _canvasRepository.ListBySessionIdAsync(SessionId)).ShouldHaveSingleItem();
        await _canvasRepository.SetClosedAtAsync(SessionId, shop.Id, "2026-09-13T10:00:00Z");

        var again = await _previews.StartAppAsync(SessionId, "npm run dev", "npm run dev", restartLive: false);

        var state = BrowserState.Parse(again.Value!.Canvas.StateJson);
        (state.Url, state.AppId).ShouldBe(("http://localhost:5173/", appId));
    }

    [Fact]
    public async Task A_refused_start_opens_no_tab()
    {
        _apps.NextRefusal = "This session already runs 3 apps, its limit.";

        var preview = await _previews.StartAppAsync(SessionId, "npm run dev", "npm run dev", restartLive: false);

        preview.Error!.Message.ShouldBe("This session already runs 3 apps, its limit.");
        (await _canvasRepository.ListBySessionIdAsync(SessionId)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(null, "npm run dev", "npm run dev")]
    [InlineData("  Shop ", "npm run dev", "Shop")]
    [InlineData(" ", " ", "Browser")]
    public void A_title_falls_back_and_is_trimmed(string? title, string fallback, string expected)
        => BrowserPreviews.TitleOr(title, fallback).ShouldBe(expected);

    [Fact]
    public void A_long_title_is_cut_to_fit_a_tab()
    {
        var title = BrowserPreviews.TitleOr(null, new string('x', 300));

        title.Length.ShouldBe(CanvasLimits.MaxTitleLength);
        title.ShouldEndWith("…");
    }
}

using System.Text.Json.Nodes;
using WeaveFleet.Application.Canvases;
using WeaveFleet.Application.Pages;
using WeaveFleet.Application.Tests.Canvases;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Pages;

public sealed class PageBridgeTests : IDisposable
{
    private const string Token = "token-1";
    private const string OpenCodeSessionId = "oc-1";
    private const string SessionId = "ses-1";
    private const string Owner = "owner-user";

    private readonly DirectoryInfo _session = Directory.CreateTempSubdirectory("fleet-page-session-");
    private readonly DirectoryInfo _mockups = Directory.CreateTempSubdirectory("fleet-page-mockups-");
    private readonly InMemoryCanvasRepository _canvasRepository = new();
    private readonly InMemorySessionRepository _sessions = new();
    private readonly ScopedUser _user = new();
    private readonly FakeCallers _callers = new();
    private readonly FakePageStore _pages = new();
    private readonly CanvasService _canvases;
    private readonly PageBridge _bridge;

    public PageBridgeTests()
    {
        _canvasRepository.AddSession(SessionId);
        _sessions.Seed(new Session { Id = SessionId, Directory = _session.FullName });
        _callers.Add(Token, OpenCodeSessionId, new HarnessCanvasCaller(SessionId, Owner));
        _canvases = new CanvasService(_canvasRepository, new FakeEventBroadcaster(), _user);
        _bridge = new PageBridge([_callers], _user, _canvases, _pages, _sessions);
    }

    public void Dispose()
    {
        _session.Delete(recursive: true);
        _mockups.Delete(recursive: true);
    }

    [Fact]
    public async Task A_page_is_copied_and_shown_in_a_page_canvas()
    {
        var file = Mockup("settings/options.html");

        var shown = await _bridge.ShowAsync(Token, OpenCodeSessionId, file, "Settings options");

        shown.IsSuccess.ShouldBeTrue(shown.Error?.Message);
        var copy = _pages.Copies.ShouldHaveSingleItem();
        copy.SessionId.ShouldBe(SessionId);
        copy.EntryFile.ShouldBe(file);
        PageIds.IsValid(copy.PageId).ShouldBeTrue();
        var canvas = (await _canvasRepository.ListBySessionIdAsync(SessionId)).ShouldHaveSingleItem();
        canvas.Kind.ShouldBe(CanvasKinds.Page);
        canvas.Title.ShouldBe("Settings options");
        canvas.UserId.ShouldBe(Owner);
        var state = PageState.Parse(canvas.StateJson);
        state.PageId.ShouldBe(copy.PageId);
        state.Entry.ShouldBe("options.html");
        state.Source.ShouldBe(file);
        state.Files.ShouldBe(2);
        state.Warnings.ShouldBeEmpty();
        shown.Value.CanvasId.ShouldBe(canvas.Id);
        shown.Value.Title.ShouldBe("Settings options · options.html");
        shown.Value.Output.ShouldStartWith($"Showing \"Settings options\" ({canvas.Id}) from {file} (2 files, 38 KB copied from its folder).");
        shown.Value.Output.ShouldContain("call fleet_page_show again with the same file");
    }

    [Fact]
    public async Task Showing_the_same_file_again_updates_its_tab_and_keeps_its_address()
    {
        var file = Mockup("options.html");
        var first = await _bridge.ShowAsync(Token, OpenCodeSessionId, file, "Options");

        var again = await _bridge.ShowAsync(Token, OpenCodeSessionId, file, "Another title");

        var canvas = (await _canvasRepository.ListBySessionIdAsync(SessionId)).ShouldHaveSingleItem();
        canvas.Title.ShouldBe("Options");
        canvas.Version.ShouldBe(first.Value!.Version!.Value + 1);
        _pages.Copies.Select(copy => copy.PageId).Distinct().ShouldHaveSingleItem();
        again.Value!.Output.ShouldStartWith("Updated \"Options\"");
        _pages.Deleted.ShouldBeEmpty();
    }

    [Fact]
    public async Task Another_file_gets_its_own_tab_named_after_it_by_default()
    {
        await _bridge.ShowAsync(Token, OpenCodeSessionId, Mockup("option-a.html"), "");
        await _bridge.ShowAsync(Token, OpenCodeSessionId, Mockup("option-b.html"), "");

        (await _canvasRepository.ListBySessionIdAsync(SessionId)).Select(canvas => canvas.Title).ShouldBe(["option-a", "option-b"], ignoreOrder: true);
        _pages.Copies.Select(copy => copy.PageId).Distinct().Count().ShouldBe(2);
    }

    [Fact]
    public async Task A_title_already_showing_another_file_moves_to_this_one_and_the_old_copy_goes()
    {
        await _bridge.ShowAsync(Token, OpenCodeSessionId, Mockup("v1/index.html"), "Mockup");
        var old = _pages.Copies.Single().PageId;

        await _bridge.ShowAsync(Token, OpenCodeSessionId, Mockup("v2/index.html"), "Mockup");

        var canvas = (await _canvasRepository.ListBySessionIdAsync(SessionId)).ShouldHaveSingleItem();
        PageState.Parse(canvas.StateJson).Source.ShouldEndWith(Path.Combine("v2", "index.html"));
        _pages.Deleted.ShouldBe([old]);
    }

    [Fact]
    public async Task A_relative_path_is_taken_from_the_session_folder()
    {
        var file = Path.Combine(_session.FullName, "mockups", "page.html");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "<p>hi</p>");

        var shown = await _bridge.ShowAsync(Token, OpenCodeSessionId, "mockups/page.html", "Page");

        shown.IsSuccess.ShouldBeTrue(shown.Error?.Message);
        _pages.Copies.ShouldHaveSingleItem().EntryFile.ShouldBe(file);
    }

    [Fact]
    public async Task A_file_that_is_not_there_is_refused()
    {
        var shown = await _bridge.ShowAsync(Token, OpenCodeSessionId, Path.Combine(_mockups.FullName, "nope.html"), "Nope");

        shown.Error!.Kind.ShouldBe(CanvasErrorKind.Invalid);
        shown.Error.Message.ShouldStartWith("There's no file at");
        _pages.Copies.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_file_that_is_not_a_page_points_diagrams_at_the_diagram_canvas()
    {
        var file = Mockup("flow.md", "# Flow");

        var shown = await _bridge.ShowAsync(Token, OpenCodeSessionId, file, "Flow");

        shown.Error!.Message.ShouldContain("isn't an HTML page");
        shown.Error.Message.ShouldContain("fleet_canvas_open");
    }

    [Fact]
    public async Task A_page_in_a_project_folder_is_sent_to_fleet_app_start()
    {
        var file = Mockup("app/index.html");
        File.WriteAllText(Path.Combine(_mockups.FullName, "app", "package.json"), "{}");

        var shown = await _bridge.ShowAsync(Token, OpenCodeSessionId, file, "App");

        shown.Error!.Kind.ShouldBe(CanvasErrorKind.Invalid);
        shown.Error.Message.ShouldContain("package.json is next to it");
        shown.Error.Message.ShouldContain("fleet_app_start");
        _pages.Copies.ShouldBeEmpty();
        (await _canvasRepository.ListBySessionIdAsync(SessionId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_page_that_loads_source_code_is_sent_to_fleet_app_start()
    {
        var file = Mockup("index.html", """<div id="app"></div><script type="module" src="/src/main.ts"></script>""");

        var shown = await _bridge.ShowAsync(Token, OpenCodeSessionId, file, "App");

        shown.Error!.Message.ShouldContain("loads /src/main.ts, which only a build or a dev server can run");
        shown.Error.Message.ShouldContain("fleet_app_start");
        _pages.Copies.ShouldBeEmpty();
    }

    [Fact]
    public async Task Links_that_will_not_load_are_shown_with_the_page_and_told_to_the_agent()
    {
        var file = Mockup("index.html", """<link rel="stylesheet" href="/styles.css"><script src="../shared.js"></script>""");

        var shown = await _bridge.ShowAsync(Token, OpenCodeSessionId, file, "Page");

        var state = PageState.Parse((await _canvasRepository.ListBySessionIdAsync(SessionId)).ShouldHaveSingleItem().StateJson);
        state.Warnings.ShouldBe([
            "/styles.css won't load: use a path relative to the page, inside its folder.",
            "../shared.js won't load: use a path relative to the page, inside its folder.",
        ]);
        shown.Value!.Output.ShouldContain("\nWarning: /styles.css won't load");
    }

    [Fact]
    public async Task A_copy_Fleet_refuses_opens_no_tab()
    {
        _pages.NextProblem = "The folder holds more than a page.";

        var shown = await _bridge.ShowAsync(Token, OpenCodeSessionId, Mockup("index.html"), "Page");

        shown.Error!.Message.ShouldBe("The folder holds more than a page.");
        (await _canvasRepository.ListBySessionIdAsync(SessionId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_title_taken_by_a_diagram_is_refused_and_the_new_copy_is_dropped()
    {
        var diagram = await _canvases.OpenAsync(SessionId, CanvasKinds.Diagram, "Plan", JsonNode.Parse("""{"nodes":[{"id":"n1","label":"A"}],"edges":[]}"""));
        diagram.IsSuccess.ShouldBeTrue(diagram.Error?.Message);

        var shown = await _bridge.ShowAsync(Token, OpenCodeSessionId, Mockup("index.html"), "Plan");

        shown.Error!.Message.ShouldContain("is already a diagram canvas");
        _pages.Deleted.ShouldBe([_pages.Copies.Single().PageId]);
    }

    [Theory]
    [InlineData(null, OpenCodeSessionId)]
    [InlineData("token-2", OpenCodeSessionId)]
    [InlineData(Token, "oc-2")]
    public async Task A_call_Fleet_cannot_place_gets_the_same_not_found(string? token, string? openCodeSessionId)
    {
        var shown = await _bridge.ShowAsync(token, openCodeSessionId, Mockup("index.html"), "Page");

        shown.Error!.Kind.ShouldBe(CanvasErrorKind.NotFound);
        shown.Error.Message.ShouldBe(CanvasBridge.UnknownCallerMessage);
        _pages.Copies.ShouldBeEmpty();
    }

    private string Mockup(string relative, string html = "<!doctype html><title>Mockup</title><p>Hello</p>")
    {
        var path = Path.Combine(_mockups.FullName, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, html);
        return path;
    }
}

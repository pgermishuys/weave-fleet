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
    private readonly ScopedUser _user = new();
    private readonly FakeCallers _callers = new();
    private readonly FakeAppRunner _apps = new();
    private readonly BrowserBridge _bridge;

    public BrowserBridgeTests()
    {
        _canvasRepository.AddSession(SessionId);
        _sessions.Seed(new Session { Id = SessionId, Directory = _folder.FullName });
        _callers.Add(Token, OpenCodeSessionId, new HarnessCanvasCaller(SessionId, Owner));
        var canvases = new CanvasService(_canvasRepository, new FakeEventBroadcaster(), _user);
        _bridge = new BrowserBridge(_callers, _user, canvases, _sessions, _apps);
    }

    public void Dispose() => _folder.Delete(recursive: true);

    [Fact]
    public async Task App_start_runs_the_command_in_the_session_folder_and_shows_the_page_it_found()
    {
        var started = await _bridge.AppStartAsync(Token, OpenCodeSessionId, " bun run dev ", "Shop");

        _apps.Started.ShouldHaveSingleItem().ShouldBe((SessionId, _folder.FullName, "bun run dev"));
        var canvas = (await _canvasRepository.ListBySessionIdAsync(SessionId)).ShouldHaveSingleItem();
        canvas.Kind.ShouldBe(CanvasKinds.Browser);
        canvas.UserId.ShouldBe(Owner);
        BrowserState.Parse(canvas.StateJson).Url.ShouldBe("http://localhost:5173/");
        BrowserState.Parse(canvas.StateJson).AppId.ShouldBe("app_1");
        started.Value!.Title.ShouldBe("Shop · http://localhost:5173/");
        started.Value.Output.ShouldStartWith($"Running `bun run dev` (app_1) in {_folder.FullName}.\nShowing http://localhost:5173/ in \"Shop\" ({canvas.Id}).");
    }

    [Fact]
    public async Task Starting_the_same_command_again_restarts_it_and_keeps_the_tab()
    {
        await _bridge.AppStartAsync(Token, OpenCodeSessionId, "bun run dev", "Shop");

        var again = await _bridge.AppStartAsync(Token, OpenCodeSessionId, "bun run dev", "Shop");

        _apps.Started.Count.ShouldBe(1);
        _apps.Restarted.ShouldBe(["app_1"]);
        again.Value!.Output.ShouldStartWith("Restarted `bun run dev` (app_1)");
        (await _canvasRepository.ListBySessionIdAsync(SessionId)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_command_that_serves_nothing_returns_the_problem_and_its_last_output_and_opens_no_tab()
    {
        _apps.NextProblem = "`bun run dev` exited with code 1 before serving a page.";
        _apps.NextLogs.AddRange(["$ bun --hot server.ts", "error: Cannot find module \"./index.html\""]);

        var started = await _bridge.AppStartAsync(Token, OpenCodeSessionId, "bun run dev", "Shop");

        started.Error!.Kind.ShouldBe(CanvasErrorKind.Invalid);
        started.Error.Message.ShouldStartWith(
            "`bun run dev` exited with code 1 before serving a page.\nLast output:\n$ bun --hot server.ts\nerror: Cannot find module \"./index.html\"");
        (await _canvasRepository.ListBySessionIdAsync(SessionId)).ShouldBeEmpty();
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
    public async Task A_call_Fleet_cannot_place_gets_the_same_not_found()
    {
        var started = await _bridge.AppStartAsync("token-2", OpenCodeSessionId, "bun run dev", "Shop");

        started.Error.ShouldBe(new CanvasError(CanvasErrorKind.NotFound, CanvasBridge.UnknownCallerMessage));
        _apps.Started.ShouldBeEmpty();
    }
}

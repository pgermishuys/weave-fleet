using System.Text.Json.Nodes;
using WeaveFleet.Application.Browser;
using WeaveFleet.Application.Canvases;
using WeaveFleet.Application.Tests.Browser;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Canvases;

public sealed class CanvasBridgeTests
{
    private const string Token = "token-1";
    private const string OpenCodeSessionId = "oc-1";
    private const string SessionId = "ses-1";
    private const string Owner = "owner-user";

    private readonly InMemoryCanvasRepository _repository = new();
    private readonly FakeEventBroadcaster _broadcaster = new();
    private readonly ScopedUser _user = new();
    private readonly FakeCallers _callers = new();
    private readonly FakeAppRunner _apps = new();
    private readonly InMemoryAppRunRepository _runs = new();
    private readonly CanvasService _canvases;
    private readonly CanvasBridge _bridge;

    public CanvasBridgeTests()
    {
        _repository.AddSession(SessionId);
        _runs.AddSession(SessionId);
        _callers.Add(Token, OpenCodeSessionId, new HarnessCanvasCaller(SessionId, Owner));
        _canvases = new CanvasService(_repository, _broadcaster, _user);
        _bridge = new CanvasBridge([_callers], _user, _canvases, new AppRunService(_apps, _runs, new InMemorySessionRepository(), _user));
    }

    private async Task<Canvas> OpenShopAsync(string title = "Shop")
    {
        var opened = await _canvases.OpenAsync(SessionId, CanvasKinds.Browser, title, JsonNode.Parse("""{ "url": "http://localhost:5173/" }"""));
        opened.IsSuccess.ShouldBeTrue(opened.Error?.Message);
        return opened.Value.Canvas;
    }

    [Theory]
    [InlineData(null, OpenCodeSessionId)]
    [InlineData("", OpenCodeSessionId)]
    [InlineData("token-2", OpenCodeSessionId)]
    [InlineData(Token, null)]
    [InlineData(Token, "oc-2")]
    public async Task A_call_Fleet_cannot_place_gets_the_same_not_found(string? token, string? openCodeSessionId)
    {
        var results = new[]
        {
            await _bridge.ListAsync(token, openCodeSessionId),
            await _bridge.ReadAsync(token, openCodeSessionId, "cv_1"),
            await _bridge.FocusAsync(token, openCodeSessionId, "cv_1"),
        };

        results.ShouldAllBe(result => result.Error == new CanvasError(CanvasErrorKind.NotFound, CanvasBridge.UnknownCallerMessage));
        (await _repository.ListBySessionIdAsync(SessionId, includeClosed: true)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_call_goes_to_whichever_harness_knows_its_token()
    {
        var otherHarness = new FakeCallers();
        otherHarness.Add("token-2", "v2-1", new HarnessCanvasCaller("ses-2", "other-owner"));
        _repository.AddSession("ses-2");
        var bridge = new CanvasBridge([_callers, otherHarness], _user, _canvases, new AppRunService(_apps, _runs, new InMemorySessionRepository(), _user));
        await OpenShopAsync();
        using (_user.Begin("other-owner"))
            (await _canvases.OpenAsync("ses-2", CanvasKinds.Browser, "Theirs", JsonNode.Parse("""{ "url": "http://localhost:3000/" }"""))).IsSuccess.ShouldBeTrue();

        (await bridge.ListAsync("token-2", "v2-1")).Value!.Output.ShouldContain("\"Theirs\"");
        (await bridge.ListAsync(Token, OpenCodeSessionId)).Value!.Output.ShouldContain("\"Shop\"");
        (await bridge.ListAsync("token-2", OpenCodeSessionId)).Error
            .ShouldBe(new CanvasError(CanvasErrorKind.NotFound, CanvasBridge.UnknownCallerMessage));
    }

    [Fact]
    public async Task List_shows_one_line_per_open_canvas()
    {
        (await _bridge.ListAsync(Token, OpenCodeSessionId)).Value.ShouldBe(new CanvasToolOutput("0 canvases", "No open canvases."));

        var shop = await OpenShopAsync();

        (await _bridge.ListAsync(Token, OpenCodeSessionId)).Value.ShouldBe(new CanvasToolOutput("1 canvas", $"{shop.Id} browser \"Shop\" v1"));

        var second = await OpenShopAsync("Admin");
        var listed = await _bridge.ListAsync(Token, OpenCodeSessionId);
        listed.Value!.Title.ShouldBe("2 canvases");
        listed.Value.Output.ShouldBe($"{shop.Id} browser \"Shop\" v1\n{second.Id} browser \"Admin\" v1");
    }

    [Fact]
    public async Task List_leaves_out_closed_canvases_and_retired_kinds()
    {
        var shop = await OpenShopAsync();
        var closed = await OpenShopAsync("Closed");
        await _canvases.CloseAsync(SessionId, closed.Id);
        await _repository.InsertAsync(
            new Canvas { Id = "cv_old", SessionId = SessionId, UserId = Owner, Kind = "diagram", Title = "Old", StateJson = "{}", Version = 1, AgentSeenVersion = 1, CreatedAt = "2026-09-12T10:00:00Z", UpdatedAt = "2026-09-12T10:00:00Z" },
            new CanvasRevision { CanvasId = "cv_old", Version = 1, Actor = "agent", OpsJson = "[]", CreatedAt = "2026-09-12T10:00:00Z" });

        (await _bridge.ListAsync(Token, OpenCodeSessionId)).Value!.Output.ShouldBe($"{shop.Id} browser \"Shop\" v1");
        (await _bridge.ReadAsync(Token, OpenCodeSessionId, "cv_old")).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task Read_returns_the_whole_canvas_as_text()
    {
        var shop = await OpenShopAsync();

        var read = await _bridge.ReadAsync(Token, OpenCodeSessionId, shop.Id);

        read.Value.ShouldBe(new CanvasToolOutput("Shop · v1", $"browser {shop.Id} \"Shop\" v1\nurl http://localhost:5173/", shop.Id, 1));
    }

    [Fact]
    public async Task Read_of_an_unknown_canvas_points_at_the_list()
    {
        var read = await _bridge.ReadAsync(Token, OpenCodeSessionId, "cv_missing");

        read.Error.ShouldBe(new CanvasError(CanvasErrorKind.NotFound, "No canvas cv_missing in this session. Call fleet_canvas_list to see the open canvases."));
    }

    [Fact]
    public async Task Focus_brings_the_canvas_forward()
    {
        var shop = await OpenShopAsync();

        var focused = await _bridge.FocusAsync(Token, OpenCodeSessionId, shop.Id);

        focused.Value.ShouldBe(new CanvasToolOutput("Shop · v1", $"Showing \"Shop\" ({shop.Id}) at v1.", shop.Id, 1));
        _broadcaster.Broadcasts.Last().Type.ShouldBe("canvas.focused");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public async Task Tools_that_name_a_canvas_need_its_id(string? canvasId)
    {
        var results = new[]
        {
            await _bridge.ReadAsync(Token, OpenCodeSessionId, canvasId),
            await _bridge.FocusAsync(Token, OpenCodeSessionId, canvasId),
        };

        results.ShouldAllBe(result => result.Error!.Kind == CanvasErrorKind.Invalid && result.Error.Message.StartsWith("\"canvasId\" is required."));
    }

    [Fact]
    public async Task Reading_a_browser_canvas_adds_its_app_status_and_recent_output()
    {
        var app = (await _apps.StartAsync(new AppRunRequest("app_1", SessionId, Owner, "/work/shop", "bun run dev"))).App!;
        await _apps.WaitUntilReadyAsync(app.Id, TimeSpan.FromSeconds(1));
        var opened = await _canvases.OpenAsync(SessionId, CanvasKinds.Browser, "Shop", JsonNode.Parse($$"""{ "url": "http://localhost:5173/", "appId": "{{app.Id}}" }"""));

        var read = await _bridge.ReadAsync(Token, OpenCodeSessionId, opened.Value!.Canvas.Id);

        read.Value!.Output.ShouldBe(
            $"browser {opened.Value.Canvas.Id} \"Shop\" v1\nurl http://localhost:5173/\napp {app.Id}\napp {app.Id} running\ncommand bun run dev\npage http://localhost:5173/\nports 5173");
    }

    [Fact]
    public async Task An_app_Fleet_ran_before_it_restarted_reads_as_stopped()
    {
        await _runs.UpsertAsync(new AppRun
        {
            Id = "app_old", SessionId = SessionId, UserId = Owner, Command = "bun run dev", Directory = "/work/shop",
            Port = 41000, Status = "stopped", Url = "http://localhost:41000/", CreatedAt = "2026-09-13T08:00:00Z", UpdatedAt = "2026-09-13T08:00:00Z",
        });
        var opened = await _canvases.OpenAsync(SessionId, CanvasKinds.Browser, "Shop", JsonNode.Parse("""{ "url": "http://localhost:41000/", "appId": "app_old" }"""));

        var read = await _bridge.ReadAsync(Token, OpenCodeSessionId, opened.Value!.Canvas.Id);

        read.Value!.Output.ShouldEndWith("app app_old stopped\ncommand bun run dev");
    }

    [Fact]
    public async Task Another_users_app_is_left_out_of_a_read()
    {
        await _apps.StartAsync(new AppRunRequest("app_theirs", SessionId, "someone-else", "/work/shop", "bun run dev"));
        var opened = await _canvases.OpenAsync(SessionId, CanvasKinds.Browser, "Shop", JsonNode.Parse("""{ "url": "http://localhost:5173/", "appId": "app_theirs" }"""));

        var read = await _bridge.ReadAsync(Token, OpenCodeSessionId, opened.Value!.Canvas.Id);

        read.Value!.Output.ShouldNotContain("command");
    }
}

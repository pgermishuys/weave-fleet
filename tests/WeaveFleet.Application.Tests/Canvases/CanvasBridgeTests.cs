using System.Text.Json.Nodes;
using WeaveFleet.Application.Browser;
using WeaveFleet.Application.Canvases;
using WeaveFleet.Application.Services;
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

    private const string Flow = """
        {
          "nodes": [
            { "id": "n1", "label": "NuCode session" },
            { "id": "n2", "label": "SessionEventsHub" }
          ],
          "edges": [{ "id": "e1", "from": "n1", "to": "n2", "label": "publishes" }]
        }
        """;

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
        _bridge = new CanvasBridge(_callers, _user, _canvases, new AppRunService(_apps, _runs, new InMemorySessionRepository(), _user));
    }

    private async Task<CanvasToolOutput> OpenFlowAsync()
    {
        var opened = await _bridge.OpenAsync(Token, OpenCodeSessionId, "diagram", "Flow", JsonNode.Parse(Flow));
        opened.IsSuccess.ShouldBeTrue(opened.Error?.Message);
        return opened.Value;
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
            await _bridge.OpenAsync(token, openCodeSessionId, "diagram", "Flow", JsonNode.Parse(Flow)),
            await _bridge.ReadAsync(token, openCodeSessionId, "cv_1"),
            await _bridge.PatchAsync(token, openCodeSessionId, "cv_1", JsonNode.Parse("[]")),
            await _bridge.FocusAsync(token, openCodeSessionId, "cv_1"),
        };

        results.ShouldAllBe(result => result.Error == new CanvasError(CanvasErrorKind.NotFound, CanvasBridge.UnknownCallerMessage));
        (await _repository.ListBySessionIdAsync(SessionId, includeClosed: true)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Open_creates_the_canvas_as_the_session_owner_and_says_where_it_is()
    {
        var opened = await OpenFlowAsync();

        var canvas = (await _repository.ListBySessionIdAsync(SessionId)).ShouldHaveSingleItem();
        canvas.UserId.ShouldBe(Owner);
        opened.ShouldBe(new CanvasToolOutput(
            "Flow · +2 boxes, +1 edge · v1",
            $"Opened \"Flow\" ({canvas.Id}) at v1.",
            canvas.Id,
            1));
        _user.UserId.ShouldBe(ScopedUser.RequestUser);
    }

    [Fact]
    public async Task Opening_the_same_title_again_reports_what_changed()
    {
        var first = await OpenFlowAsync();

        var same = await _bridge.OpenAsync(Token, OpenCodeSessionId, "diagram", "Flow", JsonNode.Parse(Flow));
        var changed = await _bridge.OpenAsync(
            Token,
            OpenCodeSessionId,
            "diagram",
            "Flow",
            JsonNode.Parse("""{ "nodes": [{ "id": "n1", "label": "NuCode session" }, { "id": "n2", "label": "SessionEventsHub" }, { "id": "n3", "label": "Client" }], "edges": [] }"""));

        same.Value!.Output.ShouldBe($"Opened \"Flow\" ({first.CanvasId}) at v1 (no changes).");
        changed.Value!.Output.ShouldBe($"Opened \"Flow\" ({first.CanvasId}) at v2 (+1 box, −1 edge).");
        changed.Value.Title.ShouldBe("Flow · +1 box, −1 edge · v2");
    }

    [Fact]
    public async Task List_shows_one_line_per_open_canvas_without_user_edit_flags()
    {
        (await _bridge.ListAsync(Token, OpenCodeSessionId)).Value.ShouldBe(new CanvasToolOutput("0 canvases", "No open canvases."));

        var opened = await OpenFlowAsync();
        (await _canvases.ApplyAsync(SessionId, opened.CanvasId!, CanvasActor.User, JsonNode.Parse("""[{ "op": "removeNode", "id": "n2" }]""")))
            .IsSuccess.ShouldBeTrue();

        var listed = await _bridge.ListAsync(Token, OpenCodeSessionId);

        listed.Value.ShouldBe(new CanvasToolOutput("1 canvas", $"{opened.CanvasId} diagram \"Flow\" v2"));
    }

    [Fact]
    public async Task Read_returns_the_whole_canvas_as_text()
    {
        var opened = await OpenFlowAsync();

        var read = await _bridge.ReadAsync(Token, OpenCodeSessionId, opened.CanvasId);

        read.Value.ShouldBe(new CanvasToolOutput(
            "Flow · v1",
            $"diagram {opened.CanvasId} \"Flow\" v1 TB\nn1 NuCode session\nn2 SessionEventsHub\ne1 n1 -> n2 publishes",
            opened.CanvasId,
            1));
    }

    [Fact]
    public async Task Patch_applies_agent_ops_and_reports_the_new_version()
    {
        var opened = await OpenFlowAsync();

        var patched = await _bridge.PatchAsync(
            Token,
            OpenCodeSessionId,
            opened.CanvasId,
            JsonNode.Parse("""[{ "op": "addNode", "id": "n3", "label": "Client" }, { "op": "addEdge", "id": "e2", "from": "n2", "to": "n3" }]"""));

        patched.Value.ShouldBe(new CanvasToolOutput("Flow · +1 box, +1 edge · v2", "Updated to v2 (+1 box, +1 edge).", opened.CanvasId, 2));
        _broadcaster.Broadcasts.Last().Type.ShouldBe("canvas.updated");
    }

    [Fact]
    public async Task Patch_passes_the_canvas_error_through_for_the_agent_to_read()
    {
        var opened = await OpenFlowAsync();

        var patched = await _bridge.PatchAsync(Token, OpenCodeSessionId, opened.CanvasId, JsonNode.Parse("""[{ "op": "updateNode", "id": "n9", "label": "x" }]"""));

        patched.Error!.Kind.ShouldBe(CanvasErrorKind.UnknownId);
        patched.Error.Message.ShouldEndWith(CanvasText.ReadHint);
    }

    [Fact]
    public async Task Focus_brings_the_canvas_forward()
    {
        var opened = await OpenFlowAsync();

        var focused = await _bridge.FocusAsync(Token, OpenCodeSessionId, opened.CanvasId);

        focused.Value.ShouldBe(new CanvasToolOutput("Flow · v1", $"Showing \"Flow\" ({opened.CanvasId}) at v1.", opened.CanvasId, 1));
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
            await _bridge.PatchAsync(Token, OpenCodeSessionId, canvasId, JsonNode.Parse("[]")),
            await _bridge.FocusAsync(Token, OpenCodeSessionId, canvasId),
        };

        results.ShouldAllBe(result => result.Error!.Kind == CanvasErrorKind.Invalid && result.Error.Message.StartsWith("\"canvasId\" is required."));
    }

    [Fact]
    public async Task Open_without_a_kind_or_title_is_rejected_with_the_service_message()
    {
        var noKind = await _bridge.OpenAsync(Token, OpenCodeSessionId, null, "Flow", JsonNode.Parse(Flow));
        var noTitle = await _bridge.OpenAsync(Token, OpenCodeSessionId, "diagram", null, JsonNode.Parse(Flow));

        noKind.Error!.Kind.ShouldBe(CanvasErrorKind.Invalid);
        noTitle.Error!.Message.ShouldBe("\"title\" must be 1-120 characters.");
    }

    [Fact]
    public async Task Reading_a_browser_canvas_adds_its_app_status_and_recent_output()
    {
        var app = (await _apps.StartAsync(new AppRunRequest("app_1", SessionId, Owner, "/work/shop", "bun run dev"))).App!;
        await _apps.WaitUntilReadyAsync(app.Id, TimeSpan.FromSeconds(1));
        var opened = await _canvases.OpenAsync(SessionId, CanvasKinds.Browser, "Shop", JsonNode.Parse($$"""{ "url": "http://localhost:5173/", "appId": "{{app.Id}}" }"""));

        var read = await _bridge.ReadAsync(Token, OpenCodeSessionId, opened.Value!.Canvas.Id);

        read.Value!.Output.ShouldBe(
            $"browser {opened.Value.Canvas.Id} \"Shop\" v1\nurl http://localhost:5173/\napp {app.Id}\napp {app.Id} running\ncommand bun run dev\nports 5173");
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

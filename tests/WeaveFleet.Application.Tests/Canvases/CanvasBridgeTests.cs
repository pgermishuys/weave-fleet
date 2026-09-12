using System.Text.Json.Nodes;
using WeaveFleet.Application.Canvases;
using WeaveFleet.Application.Services;
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
    private readonly CanvasService _canvases;
    private readonly CanvasBridge _bridge;

    public CanvasBridgeTests()
    {
        _repository.AddSession(SessionId);
        _callers.Add(Token, OpenCodeSessionId, new HarnessCanvasCaller(SessionId, Owner));
        _canvases = new CanvasService(_repository, _broadcaster, _user);
        _bridge = new CanvasBridge(_callers, _user, _canvases);
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

    /// <summary>The request's own user, until a scope says otherwise, like the real user contexts.</summary>
    private sealed class ScopedUser : IUserContext, IBackgroundUserScope
    {
        public const string RequestUser = "request-user";

        private readonly AsyncLocal<string?> _scoped = new();

        public string UserId => _scoped.Value ?? RequestUser;
        public string? Email => null;
        public string? DisplayName => UserId;
        public bool IsAuthenticated => true;

        public IDisposable Begin(string userId)
        {
            var previous = _scoped.Value;
            _scoped.Value = userId;
            return new Restore(() => _scoped.Value = previous);
        }

        private sealed class Restore(Action restore) : IDisposable
        {
            public void Dispose() => restore();
        }
    }

    private sealed class FakeCallers : IHarnessCanvasCallerResolver
    {
        private readonly Dictionary<(string Token, string SessionId), HarnessCanvasCaller> _callers = [];

        public void Add(string token, string openCodeSessionId, HarnessCanvasCaller caller) => _callers[(token, openCodeSessionId)] = caller;

        public Task<HarnessCanvasCaller?> ResolveAsync(string bridgeToken, string harnessSessionId, CancellationToken ct = default)
            => Task.FromResult(_callers.GetValueOrDefault((bridgeToken, harnessSessionId)));
    }
}

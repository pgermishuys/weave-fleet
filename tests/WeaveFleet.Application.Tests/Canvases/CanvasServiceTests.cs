using System.Text.Json;
using System.Text.Json.Nodes;
using WeaveFleet.Application.Canvases;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Canvases;

public sealed class CanvasServiceTests
{
    private const string SessionId = "ses-1";
    private const string Title = "Shop";
    private const string Shop = """{"url":"http://localhost:5173/","appId":"app_1"}""";

    private readonly InMemoryCanvasRepository _repository = new();
    private readonly FakeEventBroadcaster _broadcaster = new();
    private readonly CanvasService _service;

    public CanvasServiceTests()
    {
        _repository.AddSession(SessionId);
        _service = new CanvasService(_repository, _broadcaster, new TestUserContext());
    }

    private async Task<Canvas> OpenShopAsync()
    {
        var opened = await _service.OpenAsync(SessionId, CanvasKinds.Browser, Title, JsonNode.Parse(Shop));
        opened.IsSuccess.ShouldBeTrue(opened.Error?.Message);
        return opened.Value.Canvas;
    }

    private IReadOnlyList<string> BroadcastTypes => _broadcaster.Broadcasts.Select(b => b.Type).ToList();

    [Fact]
    public async Task OpenAsync_CreatesTheCanvasAtV1_AndBroadcastsUpdatedThenFocused()
    {
        var opened = await _service.OpenAsync(SessionId, CanvasKinds.Browser, $"  {Title} ", JsonNode.Parse(Shop));

        opened.IsSuccess.ShouldBeTrue();
        var canvas = opened.Value.Canvas;
        opened.Value.Created.ShouldBeTrue();
        canvas.Id.ShouldStartWith("cv_");
        (canvas.Title, canvas.Kind, canvas.Version, canvas.AgentSeenVersion, canvas.UserId).ShouldBe((Title, "browser", 1, 1, TestUserContext.DefaultUserId));
        opened.Value.Summary.ShouldBe("http://localhost:5173/");

        _broadcaster.Broadcasts.Count.ShouldBe(2);
        var updated = _broadcaster.Broadcasts[0];
        (updated.Topic, updated.Type, updated.UserId).ShouldBe(($"session:{SessionId}", "canvas.updated", TestUserContext.DefaultUserId));
        updated.DomainEvent.ShouldBeOfType<CanvasUpdated>();
        updated.Payload.GetProperty("sessionId").GetString().ShouldBe(SessionId);
        updated.Payload.GetProperty("canvasId").GetString().ShouldBe(canvas.Id);
        updated.Payload.GetProperty("kind").GetString().ShouldBe("browser");
        updated.Payload.GetProperty("title").GetString().ShouldBe(Title);
        updated.Payload.GetProperty("version").GetInt32().ShouldBe(1);
        updated.Payload.GetProperty("actor").GetString().ShouldBe("agent");
        updated.Payload.GetProperty("summary").GetString().ShouldBe("http://localhost:5173/");
        updated.Payload.GetProperty("state").GetProperty("url").GetString().ShouldBe("http://localhost:5173/");

        var focused = _broadcaster.Broadcasts[1];
        focused.Type.ShouldBe("canvas.focused");
        focused.DomainEvent.ShouldBeOfType<CanvasFocused>().Payload.CanvasId.ShouldBe(canvas.Id);
        focused.Payload.GetRawText().ShouldBe($$"""{"sessionId":"{{SessionId}}","canvasId":"{{canvas.Id}}"}""");
        _repository.Revisions.Select(r => (r.Version, r.Actor)).ShouldBe([(1, "agent")]);
    }

    [Fact]
    public async Task OpenAsync_InAnUnknownSession_IsNotFound()
    {
        var opened = await _service.OpenAsync("ses-missing", CanvasKinds.Browser, Title, JsonNode.Parse(Shop));

        opened.Error!.Kind.ShouldBe(CanvasErrorKind.NotFound);
        _broadcaster.Broadcasts.ShouldBeEmpty();
    }

    [Fact]
    public async Task OpenAsync_NeedsATitleOfSensibleLength()
    {
        foreach (var title in new[] { "   ", new string('x', CanvasLimits.MaxTitleLength + 1) })
        {
            var opened = await _service.OpenAsync(SessionId, CanvasKinds.Browser, title, JsonNode.Parse(Shop));

            opened.Error!.Message.ShouldBe("\"title\" must be 1-120 characters.");
        }

        (await _service.OpenAsync(SessionId, CanvasKinds.Browser, new string('x', CanvasLimits.MaxTitleLength), JsonNode.Parse(Shop)))
            .IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData("diagram")]
    [InlineData("sequence")]
    public async Task OpenAsync_NoLongerCreatesRetiredKinds(string kind)
    {
        var opened = await _service.OpenAsync(SessionId, kind, Title, JsonNode.Parse("{}"));

        opened.Error!.Kind.ShouldBe(CanvasErrorKind.Invalid);
        (await _repository.ListBySessionIdAsync(SessionId, includeClosed: true)).ShouldBeEmpty();
    }

    [Fact]
    public async Task OpenAsync_WithAnExistingTitle_ReopensAndRepointsTheCanvas()
    {
        var canvas = await OpenShopAsync();
        await _service.CloseAsync(SessionId, canvas.Id);

        var reopened = await _service.OpenAsync(SessionId, CanvasKinds.Browser, Title, JsonNode.Parse("""{"url":"http://localhost:3000/"}"""));

        reopened.IsSuccess.ShouldBeTrue(reopened.Error?.Message);
        reopened.Value.Created.ShouldBeFalse();
        reopened.Value.Canvas.Id.ShouldBe(canvas.Id);
        reopened.Value.Canvas.Version.ShouldBe(2);
        reopened.Value.Canvas.ClosedAt.ShouldBeNull();
        reopened.Value.Summary.ShouldBe("http://localhost:3000/");
        BrowserState.Parse(reopened.Value.Canvas.StateJson).Url.ShouldBe("http://localhost:3000/");

        (await _service.ListAsync(SessionId)).ShouldHaveSingleItem().Canvas.Id.ShouldBe(canvas.Id);
        BroadcastTypes.TakeLast(2).ShouldBe(["canvas.updated", "canvas.focused"]);
        _repository.Revisions.Select(r => (r.Version, r.Actor)).ShouldBe([(1, "agent"), (2, "agent")]);
    }

    [Fact]
    public async Task OpenAsync_WithAnExistingTitleAndTheSameState_OnlyFocuses()
    {
        var canvas = await OpenShopAsync();

        var again = await _service.OpenAsync(SessionId, CanvasKinds.Browser, Title, JsonNode.Parse(Shop));

        again.IsSuccess.ShouldBeTrue();
        again.Value.Canvas.Version.ShouldBe(canvas.Version);
        again.Value.Summary.ShouldBe("no changes");
        BroadcastTypes.ShouldBe(["canvas.updated", "canvas.focused", "canvas.focused"]);
    }

    [Fact]
    public async Task OpenAsync_WithAnExistingTitleAndTheSameState_ReopensAClosedCanvas()
    {
        var canvas = await OpenShopAsync();
        await _service.CloseAsync(SessionId, canvas.Id);

        var again = await _service.OpenAsync(SessionId, CanvasKinds.Browser, Title, JsonNode.Parse(Shop));

        again.Value!.Canvas.ClosedAt.ShouldBeNull();
        again.Value.Canvas.Version.ShouldBe(1);
        (await _service.ListAsync(SessionId)).ShouldHaveSingleItem();
        BroadcastTypes.TakeLast(2).ShouldBe(["canvas.updated", "canvas.focused"]);
        _broadcaster.Broadcasts[^2].DomainEvent.ShouldBeOfType<CanvasUpdated>().Payload.Summary.ShouldBe("reopened");
    }

    [Fact]
    public async Task OpenAsync_Page_ShowingAgainIsANewVersion()
    {
        var page = new PageState
        {
            PageId = "pg_" + new string('c', 32), Entry = "index.html", Source = "/tmp/m/index.html", Files = 1, Bytes = 10,
            ShownAt = "2026-09-28T10:00:00.0000000+00:00",
        };
        var first = await _service.OpenAsync(SessionId, CanvasKinds.Page, "Mockup", JsonNode.Parse(page.ToJson()));
        var second = await _service.OpenAsync(SessionId, CanvasKinds.Page, "Mockup", JsonNode.Parse(page.ToJson()));

        first.Value!.Canvas.Version.ShouldBe(1);
        second.Value!.Canvas.Version.ShouldBe(2);
        second.Value.Canvas.Id.ShouldBe(first.Value.Canvas.Id);
        second.Value.Summary.ShouldBe("1 file, 10 bytes");
    }

    [Fact]
    public async Task OpenAsync_WithAnExistingTitleOfAnotherKind_IsRejected()
    {
        var canvas = await OpenShopAsync();

        var opened = await _service.OpenAsync(SessionId, CanvasKinds.Page, Title, JsonNode.Parse("{}"));

        opened.Error!.Kind.ShouldBe(CanvasErrorKind.Invalid);
        opened.Error.Message.ShouldBe($"\"{Title}\" ({canvas.Id}) is already a browser canvas. Use another title.");
    }

    [Fact]
    public async Task OpenAsync_RetriesAgainstTheNewVersion_WhenAnotherWriterGetsThereFirst()
    {
        var canvas = await OpenShopAsync();
        _repository.BeforeNextUpdate = async () =>
            (await _service.OpenAsync(SessionId, CanvasKinds.Browser, Title, JsonNode.Parse("""{"url":"http://localhost:4000/"}"""))).IsSuccess.ShouldBeTrue();

        var reopened = await _service.OpenAsync(SessionId, CanvasKinds.Browser, Title, JsonNode.Parse("""{"url":"http://localhost:3000/"}"""));

        reopened.IsSuccess.ShouldBeTrue(reopened.Error?.Message);
        reopened.Value.Canvas.Id.ShouldBe(canvas.Id);
        reopened.Value.Canvas.Version.ShouldBe(3);
        BrowserState.Parse(reopened.Value.Canvas.StateJson).Url.ShouldBe("http://localhost:3000/");
        _repository.Revisions.Select(r => r.Version).ShouldBe([1, 2, 3]);
    }

    [Fact]
    public async Task CloseThenFocus_ClosesAndReopensTheCanvas()
    {
        var canvas = await OpenShopAsync();

        (await _service.CloseAsync(SessionId, canvas.Id)).Value!.ClosedAt.ShouldNotBeNull();
        (await _service.CloseAsync(SessionId, canvas.Id)).IsSuccess.ShouldBeTrue();
        (await _service.ListAsync(SessionId)).ShouldBeEmpty();

        (await _service.FocusAsync(SessionId, canvas.Id)).Value!.ClosedAt.ShouldBeNull();
        (await _service.ListAsync(SessionId)).ShouldHaveSingleItem();

        BroadcastTypes.Skip(2).ShouldBe(["canvas.closed", "canvas.updated", "canvas.focused"]);
        _broadcaster.Broadcasts[3].DomainEvent.ShouldBeOfType<CanvasUpdated>().Payload.Summary.ShouldBe("reopened");
    }

    [Fact]
    public async Task ReadAsync_RendersTheCanvasAndMarksItSeen()
    {
        var canvas = await OpenShopAsync();
        await _service.OpenAsync(SessionId, CanvasKinds.Browser, Title, JsonNode.Parse("""{"url":"http://localhost:3000/"}"""));
        (await _service.GetAsync(SessionId, canvas.Id))!.AgentSeenVersion.ShouldBe(2);

        var read = await _service.ReadAsync(SessionId, canvas.Id);

        read.Value.ShouldBe($"browser {canvas.Id} \"{Title}\" v2\nurl http://localhost:3000/");
    }

    [Fact]
    public async Task UnknownCanvases_AreNotFound()
    {
        (await _service.ReadAsync(SessionId, "cv_nope")).Error!.Message
            .ShouldBe("No canvas cv_nope in this session. Call fleet_canvas_list to see the open canvases.");
        (await _service.FocusAsync(SessionId, "cv_nope")).Error!.Kind.ShouldBe(CanvasErrorKind.NotFound);
        (await _service.CloseAsync(SessionId, "cv_nope")).Error!.Kind.ShouldBe(CanvasErrorKind.NotFound);
        (await _service.GetAsync(SessionId, "cv_nope")).ShouldBeNull();
    }

    [Fact]
    public async Task UpdatePageAsync_ChangesAPageInPlace_WithoutFocusing()
    {
        var page = new PageState
        {
            PageId = "pg_" + new string('d', 32), Entry = "index.html", Source = "/tmp/m/index.html", Files = 1, Bytes = 10,
            ShownAt = "2026-09-28T10:00:00.0000000+00:00",
        };
        var canvas = (await _service.OpenAsync(SessionId, CanvasKinds.Page, "Mockup", JsonNode.Parse(page.ToJson()))).Value!.Canvas;
        var before = _broadcaster.Broadcasts.Count;

        var updated = await _service.UpdatePageAsync(SessionId, canvas.Id, shown => { shown.Label = "Walkthrough"; return shown; });

        updated.Value!.Version.ShouldBe(2);
        PageState.Parse(updated.Value.StateJson).Label.ShouldBe("Walkthrough");
        _broadcaster.Broadcasts.Skip(before).Select(b => b.Type).ShouldBe(["canvas.updated"]);
        (await _service.UpdatePageAsync(SessionId, canvas.Id, _ => null)).Value!.Version.ShouldBe(2);
        (await _service.UpdatePageAsync(SessionId, "cv_nope", shown => shown)).Error!.Kind.ShouldBe(CanvasErrorKind.NotFound);
    }

    [Fact]
    public async Task ARetiredKindStoredBeforeTheUpgrade_IsHiddenFromListAndGet()
    {
        var shop = await OpenShopAsync();
        foreach (var kind in new[] { "diagram", "sequence" })
        {
            (await _repository.InsertAsync(
                new Canvas
                {
                    Id = $"cv_{kind}", SessionId = SessionId, UserId = TestUserContext.DefaultUserId, Kind = kind, Title = $"Old {kind}",
                    StateJson = "{}", Version = 1, AgentSeenVersion = 1, CreatedAt = "2026-09-12T10:00:00.0000000Z", UpdatedAt = "2026-09-12T10:00:00.0000000Z",
                },
                new CanvasRevision { CanvasId = $"cv_{kind}", Version = 1, Actor = "agent", OpsJson = "[]", CreatedAt = "2026-09-12T10:00:00.0000000Z" }))
                .ShouldBeTrue();
        }

        (await _service.ListAsync(SessionId)).Select(item => item.Canvas.Id).ShouldBe([shop.Id]);
        (await _service.GetAsync(SessionId, "cv_diagram")).ShouldBeNull();
        (await _service.GetAsync(SessionId, "cv_sequence")).ShouldBeNull();
        (await _service.GetAsync(SessionId, shop.Id)).ShouldNotBeNull();
    }
}

using System.Text.Json.Nodes;
using WeaveFleet.Application.Canvases;

namespace WeaveFleet.Application.Tests.Canvases;

public sealed class CanvasOpsTests
{
    private static readonly string PageId = "pg_" + new string('a', 32);

    private static PageState SomePage(string shownAt = "2026-09-28T10:00:00.0000000+00:00") => new()
    {
        PageId = PageId,
        Entry = "index.html",
        Source = "/tmp/m/index.html",
        Files = 4,
        Bytes = 38 * 1024,
        ShownAt = shownAt,
        Warnings = ["missing.png"],
    };

    private static JsonNode PageJson(PageState page) => JsonNode.Parse(page.ToJson())!;

    [Fact]
    public void Open_Browser_KeepsThePageAndItsApp()
    {
        var opened = CanvasOps.Open(CanvasKinds.Browser, JsonNode.Parse("""{"url":" http://localhost:5173/shop ","appId":"app_1"}"""));

        opened.IsSuccess.ShouldBeTrue(opened.Error?.Message);
        opened.Value.StateJson.ShouldBe("""{"url":"http://localhost:5173/shop","appId":"app_1"}""");
        opened.Value.Summary.ShouldBe("http://localhost:5173/shop");
        opened.Value.Ops.ShouldBe([new SetPageOp("http://localhost:5173/shop", "app_1")]);
    }

    [Theory]
    [InlineData("""{"url":"https://example.com/"}""")]
    [InlineData("""{"url":"ftp://localhost/"}""")]
    [InlineData("""{"url":"not a url"}""")]
    public void Open_Browser_OnlyTakesPagesOnThisMachine(string state)
    {
        var opened = CanvasOps.Open(CanvasKinds.Browser, JsonNode.Parse(state));

        opened.IsSuccess.ShouldBeFalse();
        opened.Error.Message.ShouldBe(LoopbackUrl.Requirement);
    }

    [Fact]
    public void Open_Browser_RejectsFieldsItDoesNotKnow()
    {
        var opened = CanvasOps.Open(CanvasKinds.Browser, JsonNode.Parse("""{"url":"http://localhost:5173/","zoom":2}"""));

        opened.Error!.Kind.ShouldBe(CanvasErrorKind.Invalid);
        opened.Error.Message.ShouldStartWith("state: unknown field \"zoom\".");
    }

    [Fact]
    public void Open_Browser_TakesStateSentAsAString()
    {
        var opened = CanvasOps.Open(CanvasKinds.Browser, JsonValue.Create("""{"url":"http://localhost:5173/"}"""));

        opened.IsSuccess.ShouldBeTrue(opened.Error?.Message);
        opened.Value.StateJson.ShouldBe("""{"url":"http://localhost:5173/"}""");
    }

    [Fact]
    public void Open_Page_KeepsWhatFleetCopied()
    {
        var opened = CanvasOps.Open(CanvasKinds.Page, PageJson(SomePage()));

        opened.IsSuccess.ShouldBeTrue(opened.Error?.Message);
        var page = PageState.Parse(opened.Value.StateJson);
        (page.PageId, page.Entry, page.Source, page.Files, page.Bytes).ShouldBe((PageId, "index.html", "/tmp/m/index.html", 4, 38L * 1024));
        page.Warnings.ShouldBe(["missing.png"]);
        opened.Value.Summary.ShouldBe("4 files, 38 KB");
    }

    [Fact]
    public void Open_Page_NeedsAPageFleetCopied()
    {
        var page = SomePage();
        page.PageId = "pg_x";

        var opened = CanvasOps.Open(CanvasKinds.Page, PageJson(page));

        opened.Error!.Kind.ShouldBe(CanvasErrorKind.Invalid);
        opened.Error.Message.ShouldContain("fleet_page_show");
    }

    [Fact]
    public void Open_Page_NeedsAnObject()
    {
        var opened = CanvasOps.Open(CanvasKinds.Page, JsonNode.Parse("[]"));

        opened.Error!.Message.ShouldBe("\"state\" must be an object with \"pageId\", \"entry\" and \"source\".");
    }

    [Theory]
    [InlineData("diagram")]
    [InlineData("sequence")]
    [InlineData("markdown")]
    public void Open_AnyOtherKind_IsRejected(string kind)
    {
        var opened = CanvasOps.Open(kind, JsonNode.Parse("{}"));

        opened.IsSuccess.ShouldBeFalse();
        opened.Error.Kind.ShouldBe(CanvasErrorKind.Invalid);
        opened.Error.Message.ShouldBe($"Unknown canvas kind \"{kind}\". Use browser or page.");
    }

    [Fact]
    public void Replace_Browser_ChangesOnlyWhenThePageOrAppChanges()
    {
        var current = CanvasOps.Open(CanvasKinds.Browser, JsonNode.Parse("""{"url":"http://localhost:5173/","appId":"app_1"}""")).Value!.StateJson;

        CanvasOps.Replace(CanvasKinds.Browser, current, JsonNode.Parse("""{"url":"http://localhost:5173/","appId":"app_1"}""")).Value.ShouldBeEmpty();
        CanvasOps.Replace(CanvasKinds.Browser, current, JsonNode.Parse("""{"url":"http://localhost:5173/","appId":"app_2"}""")).Value
            .ShouldHaveSingleItem().ShouldBe(new SetPageOp("http://localhost:5173/", "app_2"));
    }

    [Fact]
    public void Replace_Page_IsAlwaysANewShow_EvenOfTheSameCopy()
    {
        var current = CanvasOps.Open(CanvasKinds.Page, PageJson(SomePage())).Value!.StateJson;

        var ops = CanvasOps.Replace(CanvasKinds.Page, current, PageJson(SomePage())).Value;

        ops.ShouldHaveSingleItem().ShouldBeOfType<ShowPageOp>().Page.PageId.ShouldBe(PageId);
    }

    [Fact]
    public void Replace_PassesOnTheOpenError()
    {
        var replaced = CanvasOps.Replace(CanvasKinds.Browser, """{"url":"http://localhost:5173/"}""", JsonNode.Parse("""{"url":"https://example.com/"}"""));

        replaced.Error!.Message.ShouldBe(LoopbackUrl.Requirement);
    }

    [Fact]
    public void Apply_Browser_TakesTheLastPage()
    {
        var applied = CanvasOps.Apply(
            CanvasKinds.Browser,
            """{"url":"http://localhost:5173/"}""",
            [new SetPageOp("http://localhost:3000/", null), new SetPageOp("http://localhost:4000/a", "app_9")]);

        applied.IsSuccess.ShouldBeTrue(applied.Error?.Message);
        applied.Value.StateJson.ShouldBe("""{"url":"http://localhost:4000/a","appId":"app_9"}""");
        applied.Value.Summary.ShouldBe("http://localhost:4000/a");
    }

    [Fact]
    public void Apply_Page_ShowsTheGivenPage()
    {
        var applied = CanvasOps.Apply(CanvasKinds.Page, new PageState().ToJson(), [new ShowPageOp(SomePage())]);

        applied.IsSuccess.ShouldBeTrue(applied.Error?.Message);
        PageState.Parse(applied.Value.StateJson).Entry.ShouldBe("index.html");
        applied.Value.Summary.ShouldBe("4 files, 38 KB");
    }

    [Fact]
    public void Apply_RejectsAnOpTheKindDoesNotTake()
    {
        var onBrowser = CanvasOps.Apply(CanvasKinds.Browser, "{}", [new ShowPageOp(SomePage())]);
        var onPage = CanvasOps.Apply(CanvasKinds.Page, "{}", [new SetPageOp("http://localhost:5173/", null)]);

        onBrowser.Error!.Message.ShouldBe("showPage doesn't apply to a browser canvas.");
        onPage.Error!.Message.ShouldBe("setPage doesn't apply to a page canvas.");
    }

    [Fact]
    public void Apply_NeedsAtLeastOneOp()
    {
        CanvasOps.Apply(CanvasKinds.Browser, "{}", []).Error!.Message.ShouldBe("No changes given.");
    }

    [Fact]
    public void Apply_ValidatesTheResultingState()
    {
        var applied = CanvasOps.Apply(CanvasKinds.Browser, "{}", [new SetPageOp("https://example.com/", null)]);

        applied.Error!.Message.ShouldBe(LoopbackUrl.Requirement);
    }

    [Fact]
    public void Apply_Page_RejectsAStateOverTheLimit()
    {
        var page = SomePage();
        page.Warnings = [new string('x', CanvasLimits.MaxStateBytes)];

        var applied = CanvasOps.Apply(CanvasKinds.Page, "{}", [new ShowPageOp(page)]);

        applied.Error!.Kind.ShouldBe(CanvasErrorKind.TooLarge);
    }

    [Fact]
    public void Apply_AnUnknownKind_IsRejected()
    {
        CanvasOps.Apply("diagram", "{}", [new SetPageOp("http://localhost:5173/", null)]).Error!.Message
            .ShouldBe("Unknown canvas kind \"diagram\". Use browser or page.");
    }

    [Fact]
    public void ToJson_RecordsEachOpAsItWasApplied()
    {
        var json = CanvasOps.ToJson([
            new SetPageOp("http://localhost:5173/", "app_1"),
            new SetPageOp("http://localhost:3000/", null),
            new ShowPageOp(SomePage()),
        ]);

        var ops = JsonNode.Parse(json)!.AsArray();
        ops.Count.ShouldBe(3);
        ops[0]!.ToJsonString().ShouldBe("""{"op":"setPage","url":"http://localhost:5173/","appId":"app_1"}""");
        ops[1]!.ToJsonString().ShouldBe("""{"op":"setPage","url":"http://localhost:3000/"}""");
        ops[2]!["op"]!.GetValue<string>().ShouldBe("showPage");
        ops[2]!["pageId"]!.GetValue<string>().ShouldBe(PageId);
        ops[2]!["warnings"]!.AsArray().Single()!.GetValue<string>().ShouldBe("missing.png");
    }

    [Theory]
    [InlineData("browser", true)]
    [InlineData("page", true)]
    [InlineData("diagram", false)]
    [InlineData("sequence", false)]
    public void OnlyBrowserAndPageKindsAreKnown(string kind, bool known)
        => CanvasKinds.IsKnown(kind).ShouldBe(known);

    [Fact]
    public void EmptyState_ForAnUnknownKind_Throws()
        => Should.Throw<ArgumentException>(() => CanvasOps.EmptyState("diagram"));
}

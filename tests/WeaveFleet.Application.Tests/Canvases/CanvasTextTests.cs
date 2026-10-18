using WeaveFleet.Application.Canvases;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Application.Tests.Canvases;

public sealed class CanvasTextTests
{
    private static Canvas Browser(string stateJson, string title = "Shop") => new()
    {
        Id = "cv_01J",
        Kind = CanvasKinds.Browser,
        Title = title,
        Version = 5,
        StateJson = stateJson,
    };

    [Fact]
    public void RenderFull_Browser_IsThePageAndItsApp()
    {
        CanvasText.RenderFull(Browser("""{"url":"http://localhost:5173/","appId":"app_1"}"""))
            .ShouldBe("browser cv_01J \"Shop\" v5\nurl http://localhost:5173/\napp app_1");
    }

    [Fact]
    public void RenderFull_Browser_WithoutAnApp_LeavesTheAppLineOut()
    {
        CanvasText.RenderFull(Browser("""{"url":"http://localhost:5173/"}""", title: "Say \"hi\""))
            .ShouldBe("browser cv_01J \"Say \\\"hi\\\"\" v5\nurl http://localhost:5173/");
    }

    [Fact]
    public void RenderFull_Page_ListsTheSourceTheCopyAndItsWarnings()
    {
        var canvas = new Canvas
        {
            Id = "cv_02",
            Kind = CanvasKinds.Page,
            Title = "Mockup",
            Version = 3,
            StateJson = new PageState
            {
                PageId = "pg_" + new string('a', 32), Entry = "index.html", Source = "/tmp/m/index.html",
                Files = 4, Bytes = 38 * 1024, ShownAt = "2026-09-28T10:00:00.0000000+00:00", Warnings = ["missing.png"],
            }.ToJson(),
        };

        CanvasText.RenderFull(canvas).ShouldBe(
            "page cv_02 \"Mockup\" v3\nsource /tmp/m/index.html\ncopied 4 files, 38 KB at 2026-09-28T10:00:00.0000000+00:00\nwarning missing.png");
    }

    [Fact]
    public void RenderListLine_IsIdKindTitleAndVersion()
    {
        CanvasText.RenderListLine(Browser("{}")).ShouldBe("cv_01J browser \"Shop\" v5");
    }

    [Fact]
    public void Summarize_NamesThePageOrTheCopy()
    {
        CanvasText.Summarize([new SetPageOp("http://localhost:5173/", null)]).ShouldBe("http://localhost:5173/");
        CanvasText.Summarize([new ShowPageOp(new PageState { Files = 1, Bytes = 10 })]).ShouldBe("1 file, 10 bytes");
        CanvasText.Summarize([]).ShouldBe("no changes");
    }

    [Theory]
    [InlineData(1, 0L, "1 file, 0 bytes")]
    [InlineData(2, 1023L, "2 files, 1023 bytes")]
    [InlineData(3, 1025L, "3 files, 2 KB")]
    [InlineData(1, 3L * 1024 * 1024, "1 file, 3 MB")]
    public void PageSize_PicksAUnit(int files, long bytes, string expected)
        => CanvasText.PageSize(files, bytes).ShouldBe(expected);
}

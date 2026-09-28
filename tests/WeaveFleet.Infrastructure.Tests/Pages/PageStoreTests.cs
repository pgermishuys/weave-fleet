using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Pages;
using WeaveFleet.Infrastructure.Pages;

namespace WeaveFleet.Infrastructure.Tests.Pages;

public sealed class PageStoreTests : IDisposable
{
    private const string Session = "ses-1";

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("fleet-pages-");
    private readonly DirectoryInfo _source = Directory.CreateTempSubdirectory("fleet-page-source-");
    private readonly PageStore _store;

    public PageStoreTests() => _store = new PageStore(_root.FullName, NullLogger<PageStore>.Instance);

    public void Dispose()
    {
        _root.Delete(recursive: true);
        _source.Delete(recursive: true);
    }

    [Fact]
    public async Task A_page_is_copied_with_the_web_files_in_its_folder_and_nothing_else()
    {
        Write("index.html", "<link href=\"css/site.css\" rel=\"stylesheet\"><p>Options</p>");
        Write("css/site.css", "body { color: red }");
        Write("img/logo.svg", "<svg/>");
        Write("notes.md", "# private");
        Write(".env", "SECRET=1");
        Write(".git/config", "[core]");
        Write("node_modules/lib/index.js", "x");
        var pageId = PageIds.New();

        var copied = await _store.CopyAsync(Session, pageId, Path.Combine(_source.FullName, "index.html"));

        copied.Problem.ShouldBeNull();
        copied.Page.ShouldBe(new PageCopy(pageId, "index.html", 3, copied.Page!.Bytes));
        copied.Page.Bytes.ShouldBeGreaterThan(0);
        File.ReadAllText(_store.Resolve(pageId, "")!).ShouldContain("Options");
        File.ReadAllText(_store.Resolve(pageId, "css/site.css")!).ShouldBe("body { color: red }");
        _store.Resolve(pageId, "img/logo.svg").ShouldNotBeNull();
        _store.Resolve(pageId, "notes.md").ShouldBeNull();
        _store.Resolve(pageId, ".env").ShouldBeNull();
        _store.Resolve(pageId, ".entry").ShouldBeNull();
        _store.Resolve(pageId, "node_modules/lib/index.js").ShouldBeNull();
    }

    [Fact]
    public async Task Copying_again_replaces_the_page_so_removed_files_go_too()
    {
        Write("index.html", "<p>one</p>");
        Write("old.css", "a {}");
        var pageId = PageIds.New();
        await _store.CopyAsync(Session, pageId, Path.Combine(_source.FullName, "index.html"));
        File.Delete(Path.Combine(_source.FullName, "old.css"));
        Write("index.html", "<p>two</p>");

        var again = await _store.CopyAsync(Session, pageId, Path.Combine(_source.FullName, "index.html"));

        again.Page!.Files.ShouldBe(1);
        File.ReadAllText(_store.Resolve(pageId, "")!).ShouldBe("<p>two</p>");
        _store.Resolve(pageId, "old.css").ShouldBeNull();
        Directory.GetDirectories(Path.Combine(_root.FullName, Session)).Select(Path.GetFileName).ShouldBe([pageId]);
    }

    [Theory]
    [InlineData("../other/index.html")]
    [InlineData("css/../../index.html")]
    [InlineData("css//site.css")]
    [InlineData("..\\index.html")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("missing.html")]
    public async Task A_path_that_leaves_the_page_or_is_not_there_resolves_to_nothing(string path)
    {
        Write("index.html", "<p>hi</p>");
        Write("css/site.css", "a {}");
        var pageId = PageIds.New();
        await _store.CopyAsync(Session, pageId, Path.Combine(_source.FullName, "index.html"));

        _store.Resolve(pageId, path).ShouldBeNull();
    }

    [Fact]
    public void An_unknown_or_malformed_page_id_resolves_to_nothing()
    {
        _store.Resolve(PageIds.New(), "").ShouldBeNull();
        _store.Resolve("pg_..", "").ShouldBeNull();
        _store.Resolve("../ses-1", "").ShouldBeNull();
    }

    [Fact]
    public async Task A_page_is_found_again_after_Fleet_restarts()
    {
        Write("index.html", "<p>hi</p>");
        var pageId = PageIds.New();
        await _store.CopyAsync(Session, pageId, Path.Combine(_source.FullName, "index.html"));

        var restarted = new PageStore(_root.FullName, NullLogger<PageStore>.Instance);

        restarted.Resolve(pageId, "index.html").ShouldNotBeNull();
    }

    [Fact]
    public async Task A_folder_with_more_files_than_a_page_is_refused_and_nothing_is_kept()
    {
        Write("index.html", "<p>hi</p>");
        for (var i = 0; i < PageRules.MaxFiles; i++)
            Write($"data/{i}.json", "{}");
        var pageId = PageIds.New();

        var copied = await _store.CopyAsync(Session, pageId, Path.Combine(_source.FullName, "index.html"));

        copied.Page.ShouldBeNull();
        copied.Problem!.ShouldContain("Put the page in a folder of its own");
        _store.Resolve(pageId, "").ShouldBeNull();
    }

    [Fact]
    public async Task Deleting_a_page_or_its_session_removes_the_copies()
    {
        Write("index.html", "<p>hi</p>");
        var first = PageIds.New();
        var second = PageIds.New();
        await _store.CopyAsync(Session, first, Path.Combine(_source.FullName, "index.html"));
        await _store.CopyAsync(Session, second, Path.Combine(_source.FullName, "index.html"));

        await _store.DeleteAsync(Session, first);
        _store.Resolve(first, "").ShouldBeNull();
        _store.Resolve(second, "").ShouldNotBeNull();

        await _store.DeleteSessionAsync(Session);
        _store.Resolve(second, "").ShouldBeNull();
        Directory.Exists(Path.Combine(_root.FullName, Session)).ShouldBeFalse();
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_source.FullName, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}

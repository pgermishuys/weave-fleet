using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using WeaveFleet.Application.Canvases;
using WeaveFleet.Application.Git;
using WeaveFleet.Application.Pages;
using WeaveFleet.Application.Tests.Canvases;
using WeaveFleet.Application.Tests.Pages;
using WeaveFleet.Application.Walkthroughs;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Walkthroughs;

/// <summary>
/// fleet_walkthrough_show against a real repository: the guide's chapters get the change's own hunks, files it names that
/// aren't in the change are refused, and the files it leaves out are said.
/// </summary>
public sealed partial class WalkthroughBridgeTests : IAsyncLifetime
{
    private const string Token = "token-1";
    private const string HarnessSessionId = "harness-1";
    private const string SessionId = "ses-1";
    private const string Owner = "owner-user";

    private readonly DirectoryInfo _repo = Directory.CreateTempSubdirectory("fleet-walkthrough-repo-");
    private readonly InMemoryCanvasRepository _canvasRepository = new();
    private readonly InMemorySessionRepository _sessions = new();
    private readonly ScopedUser _user = new();
    private readonly FakeCallers _callers = new();
    private readonly FakePageStore _pages = new();
    private WalkthroughBridge _bridge = null!;

    public async Task InitializeAsync()
    {
        var root = _repo.FullName;
        await GitAsync("init", "-b", "main");
        await WriteAsync("src/orders.cs", "class Orders\n{\n    void Ship() => From(Default);\n}\n");
        await WriteAsync("README.md", "Rocket shop\n");
        await CommitAllAsync("initial");
        var baseline = (await GitAsync("rev-parse", "HEAD")).Trim();

        // The session's change: one file edited, one added and not yet tracked, one binary.
        await WriteAsync("src/orders.cs", "class Orders\n{\n    void Ship() => From(Warehouses.Nearest(Address));\n}\n");
        await WriteAsync("src/warehouses.cs", "static class Warehouses\n{\n    public static Warehouse Nearest(Address to) => All.MinBy(w => w.DistanceTo(to));\n}\n");
        await File.WriteAllBytesAsync(Path.Combine(root, "shot.png"), [0x89, 0x50, 0x4E, 0x47, 0x00, 0x01]);

        _canvasRepository.AddSession(SessionId);
        _sessions.Seed(new Session { Id = SessionId, Directory = root, GitRepoRoot = root, GitBaselineRef = baseline });
        _callers.Add(Token, HarnessSessionId, new HarnessCanvasCaller(SessionId, Owner));
        var canvases = new CanvasService(_canvasRepository, new FakeEventBroadcaster(), _user);
        var pages = new PageBridge([_callers], _user, canvases, _pages, _sessions);
        _bridge = new WalkthroughBridge([_callers], _user, _sessions, new GitDiffService(), pages);
    }

    public Task DisposeAsync()
    {
        foreach (var file in Directory.EnumerateFiles(_repo.FullName, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        _repo.Delete(recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Each_chapter_gets_the_hunks_of_its_files_and_the_page_shows_in_a_page_tab()
    {
        var shown = await ShowAsync("Orders ship from the nearest warehouse", """
            {"summary": "Orders ship from the nearest warehouse, not the default one.",
             "chapters": [
               {"title": "Find the nearest warehouse", "body": "`Nearest` picks the closest.", "files": ["src/warehouses.cs"]},
               {"title": "Ship from it", "body": "`Ship` asks for it.", "files": ["src/orders.cs"]}]}
            """);

        shown.IsSuccess.ShouldBeTrue(shown.Error?.Message);
        var canvas = (await _canvasRepository.ListBySessionIdAsync(SessionId)).ShouldHaveSingleItem();
        canvas.Kind.ShouldBe(CanvasKinds.Page);
        canvas.Title.ShouldBe("Orders ship from the nearest warehouse");
        var state = PageState.Parse(canvas.StateJson);
        state.Source.ShouldBe("walkthrough:Orders ship from the nearest warehouse");
        state.Label.ShouldBe("Walkthrough · since this session started");
        shown.Value!.Title.ShouldBe("Orders ship from the nearest warehouse · 2 chapters");

        var page = PageData(_pages.Texts.ShouldHaveSingleItem());
        page.GetProperty("comparedWith").GetString().ShouldBe("since this session started");
        page.GetProperty("guide").GetProperty("chapters").EnumerateArray().Select(c => c.GetProperty("files")[0].GetProperty("path").GetString())
            .ShouldBe(["src/warehouses.cs", "src/orders.cs"]);

        var files = page.GetProperty("files").EnumerateArray().ToDictionary(f => f.GetProperty("path").GetString()!);
        files.Keys.ShouldBe(["shot.png", "src/orders.cs", "src/warehouses.cs"], ignoreOrder: false);
        files["src/orders.cs"].GetProperty("patch").GetString()!.ShouldContain("-    void Ship() => From(Default);\n+    void Ship() => From(Warehouses.Nearest(Address));");
        // Not tracked yet, so git has no diff for it: Fleet writes it as lines added.
        files["src/warehouses.cs"].GetProperty("patch").GetString()!.ShouldStartWith("@@ -0,0 +1,4 @@\n+static class Warehouses\n");
        files["src/warehouses.cs"].GetProperty("status").GetString().ShouldBe("added");
        files["shot.png"].GetProperty("binary").GetBoolean().ShouldBeTrue();
        page.GetProperty("uncovered").EnumerateArray().Select(p => p.GetString()).ShouldBe(["shot.png"]);

        var output = shown.Value!.Output;
        output.ShouldStartWith($"Showing \"Orders ship from the nearest warehouse\" ({canvas.Id}): 2 chapters over 2 of the 3 changed files (since this session started).");
        output.ShouldContain("Not in any chapter or alsoChanged, so listed under \"Not in the walkthrough\": shot.png.");
    }

    [Fact]
    public async Task A_file_that_isnt_in_the_change_is_refused_with_the_files_that_are()
    {
        var shown = await ShowAsync("Orders", """
            {"summary": "x", "chapters": [{"title": "t", "body": "b", "files": ["src/payments.cs", "./src/orders.cs"]}]}
            """);

        shown.IsSuccess.ShouldBeFalse();
        shown.Error!.Message.ShouldBe(
            "Not in the change (since this session started): src/payments.cs. Name files as git does, from the repository's root. "
            + "The change has: shot.png, src/orders.cs, src/warehouses.cs.");
        _pages.Copies.ShouldBeEmpty();
    }

    [Fact]
    public async Task Also_changed_takes_patterns_and_says_which_matched_nothing()
    {
        var shown = await ShowAsync("Orders", """
            {"summary": "x", "chapters": [{"title": "t", "body": "b", "files": ["src/*.cs"]}]}
            """);
        shown.Error!.Message.ShouldStartWith("Not in the change (since this session started): src/*.cs.");

        shown = await ShowAsync("Orders", """
            {"summary": "x", "chapters": [{"title": "t", "body": "b", "files": ["src/orders.cs", "src/warehouses.cs"]}],
             "alsoChanged": [{"path": "*.png", "note": "a screenshot"}, "docs/**"]}
            """);

        shown.IsSuccess.ShouldBeTrue(shown.Error?.Message);
        PageData(_pages.Texts.Last()).GetProperty("uncovered").GetArrayLength().ShouldBe(0);
        shown.Value!.Output.ShouldContain("alsoChanged matched no changed file: docs/**.");
        shown.Value.Output.ShouldNotContain("Not in the walkthrough");
    }

    [Fact]
    public async Task The_same_title_updates_its_tab()
    {
        const string Guide = """{"summary": "x", "chapters": [{"title": "t", "body": "b", "files": ["src/orders.cs"]}]}""";

        (await ShowAsync("Orders", Guide)).Value!.Output.ShouldStartWith("Showing ");
        (await ShowAsync("Orders", Guide)).Value!.Output.ShouldStartWith("Updated ");

        (await _canvasRepository.ListBySessionIdAsync(SessionId)).ShouldHaveSingleItem();
        _pages.Copies.Select(c => c.PageId).Distinct().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task From_and_to_show_what_a_branch_adds_and_leave_the_folder_out()
    {
        await GitAsync("checkout", "-q", "-b", "feature/express");
        await WriteAsync("src/express.cs", "class Express { }\n");
        await GitAsync("add", "src/express.cs");
        await GitAsync("-c", "user.name=Weave Test", "-c", "user.email=weave@example.invalid", "commit", "-q", "-m", "express");

        var shown = await ShowAsync("Express shipping", """
            {"summary": "x", "chapters": [{"title": "t", "body": "b", "files": ["src/express.cs"]}], "from": "main", "to": "feature/express"}
            """);

        shown.IsSuccess.ShouldBeTrue(shown.Error?.Message);
        var page = PageData(_pages.Texts.Single());
        page.GetProperty("comparedWith").GetString().ShouldBe("feature/express against main");
        // The edits still in the folder aren't part of the branch's commits.
        page.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("path").GetString()).ShouldBe(["src/express.cs"]);
    }

    [Fact]
    public async Task A_reference_that_isnt_one_is_refused()
    {
        var shown = await ShowAsync("Orders", """
            {"summary": "x", "chapters": [{"title": "t", "body": "b", "files": ["src/orders.cs"]}], "from": "--output=/tmp/x"}
            """);

        shown.Error!.Message.ShouldStartWith("guide.from \"--output=/tmp/x\" isn't a branch, tag or commit in this repository.");
    }

    [Fact]
    public async Task A_guide_that_isnt_ready_says_what_to_fix()
    {
        var shown = await ShowAsync("Orders", """{"chapters": []}""");

        shown.Error!.Message.ShouldStartWith("The guide isn't ready:\n- guide.summary is required");
        shown.Error.Message.ShouldEndWith("Fix these and call fleet_walkthrough_show again.");
    }

    [Fact]
    public async Task A_guide_sent_as_a_string_of_JSON_is_read()
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize("""{"summary": "x", "chapters": [{"title": "t", "body": "b", "files": ["src/orders.cs"]}]}"""));

        var shown = await _bridge.ShowAsync(Token, HarnessSessionId, "Orders", document.RootElement.Clone());

        shown.IsSuccess.ShouldBeTrue(shown.Error?.Message);
    }

    [Fact]
    public void The_page_data_cant_end_its_script()
    {
        var html = WalkthroughBridge.Html("A </title> title", new System.Text.Json.Nodes.JsonObject { ["summary"] = "</script><script>alert(1)</script>" });

        html.ShouldContain("<title>A &lt;/title&gt; title</title>");
        DataSlot().Match(html).Groups[1].Value.ShouldNotContain("</script>");
    }

    private async Task<CanvasResult<CanvasToolOutput>> ShowAsync(string title, string guide)
    {
        using var document = JsonDocument.Parse(guide);
        return await _bridge.ShowAsync(Token, HarnessSessionId, title, document.RootElement.Clone());
    }

    private static JsonElement PageData(string html)
    {
        using var document = JsonDocument.Parse(DataSlot().Match(html).Groups[1].Value);
        return document.RootElement.Clone();
    }

    [GeneratedRegex("<script id=\"walkthrough-data\" type=\"application/json\">(.*?)</script>", RegexOptions.Singleline)]
    private static partial Regex DataSlot();

    private async Task WriteAsync(string path, string content)
    {
        var full = Path.Combine(_repo.FullName, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, content);
    }

    private async Task CommitAllAsync(string message)
    {
        await GitAsync("add", ".");
        await GitAsync("-c", "user.name=Weave Test", "-c", "user.email=weave@example.invalid", "commit", "-q", "-m", message);
    }

    private async Task<string> GitAsync(params string[] arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = _repo.FullName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        process.ExitCode.ShouldBe(0, $"git {string.Join(' ', arguments)}: {error}");
        return output;
    }
}

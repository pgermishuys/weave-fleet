using System.Text.Json;
using WeaveFleet.Application.Walkthroughs;

namespace WeaveFleet.Application.Tests.Walkthroughs;

/// <summary>The outline an agent sends with fleet_walkthrough_show, and what Fleet tells it when the outline isn't right.</summary>
public sealed class WalkthroughGuideTests
{
    [Fact]
    public void A_guide_reads_its_chapters_steps_and_diagram()
    {
        var (guide, errors) = Parse("""
            {
              "summary": "Orders ship from the nearest warehouse.",
              "steps": ["Find the warehouse.", {"text": "Ship from it.", "chapter": 2}],
              "chapters": [
                {"title": "Find the nearest warehouse", "body": "`Nearest` picks it.", "cite": "src/warehouse.cs:3", "files": ["src/warehouse.cs"]},
                {"title": "Ship from it", "body": ["One.", "Two."], "files": [{"path": "src/orders.cs"}, {"path": "tests/orders_test.cs", "collapsed": true}], "closer": ["Is a tie broken the same way twice?"]}
              ],
              "diagram": {"caption": "Where an order ships from", "after": {"rows": [{"nodes": [{"text": "Ship", "code": true, "chapter": 2, "kind": "new"}]}]}},
              "alsoChanged": ["docs/*.png", {"path": "package-lock.json", "note": "lock file"}]
            }
            """);

        errors.ShouldBeEmpty();
        guide.ShouldNotBeNull();
        guide.Steps.ShouldBe([new WalkthroughStep("Find the warehouse.", null), new WalkthroughStep("Ship from it.", 2)]);
        guide.Chapters.Select(c => c.Title).ShouldBe(["Find the nearest warehouse", "Ship from it"]);
        guide.Chapters[0].Body.ShouldBe(["`Nearest` picks it."]);
        guide.Chapters[1].Files.ShouldBe([new WalkthroughFile("src/orders.cs", false), new WalkthroughFile("tests/orders_test.cs", true)]);
        guide.Chapters[1].Closer.ShouldBe(["Is a tie broken the same way twice?"]);
        guide.AlsoChanged.ShouldBe([new WalkthroughAlsoChanged("docs/*.png", null), new WalkthroughAlsoChanged("package-lock.json", "lock file")]);
        guide.Diagram!["after"]!["rows"]![0]!["nodes"]![0]!["chapter"]!.GetValue<int>().ShouldBe(2);
        guide.From.ShouldBeNull();
    }

    [Fact]
    public void What_is_missing_is_said_in_words_with_where_it_is()
    {
        var (guide, errors) = Parse("""
            {
              "chapters": [{"title": "", "body": [], "files": []}],
              "steps": [{"text": "Too far.", "chapter": 3}],
              "to": "feature"
            }
            """);

        guide.ShouldBeNull();
        errors.ShouldContain("guide.summary is required: two or three sentences on what the change does and why.");
        errors.ShouldContain("guide.chapters[0].title is required.");
        errors.ShouldContain("guide.chapters[0].body is required: what changed in plain words, and why it's here.");
        errors.ShouldContain("guide.chapters[0].files is required: the changed files this chapter is about.");
        errors.ShouldContain("guide.steps[0].chapter must be a chapter number from 1 to 1.");
        errors.ShouldContain("guide.to needs guide.from: the walkthrough shows from...to.");
    }

    [Fact]
    public void A_guide_needs_chapters_and_not_too_many()
    {
        Parse("""{"summary": "x"}""").Errors.ShouldBe(["guide.chapters is required: one or more chapters, each {\"title\", \"body\", \"files\"}."]);

        var many = string.Join(",", Enumerable.Range(0, WalkthroughGuide.MaxChapters + 1).Select(i => $$"""{"title": "c{{i}}", "body": "b", "files": ["f"]}"""));
        Parse($$"""{"summary": "x", "chapters": [{{many}}]}""").Errors.ShouldHaveSingleItem().ShouldContain("30 at most");
    }

    [Fact]
    public void A_diagram_node_names_a_chapter_that_exists()
    {
        var (_, errors) = Parse("""
            {"summary": "x", "chapters": [{"title": "t", "body": "b", "files": ["f"]}],
             "diagram": {"after": {"rows": [{"nodes": [{"text": "A", "chapter": 2}, {"note": "no text"}]}]}}}
            """);

        errors.ShouldBe(
        [
            "guide.diagram.after.rows[0].nodes[0].chapter must be a chapter number from 1 to 1.",
            "guide.diagram.after.rows[0].nodes[1] needs \"text\".",
        ]);
    }

    [Fact]
    public void Also_changed_takes_a_path_and_note_as_a_pair_too()
    {
        var (guide, errors) = Parse("""
            {"summary": "x", "chapters": [{"title": "t", "body": "b", "files": ["f"]}],
             "alsoChanged": [["package-lock.json", "lock file"], ["docs/*.png"], {"note": "no path"}]}
            """);
        guide.ShouldBeNull();
        errors.ShouldBe(["guide.alsoChanged[2] must be a path, or {\"path\", \"note\"}."]);

        (guide, _) = Parse("""
            {"summary": "x", "chapters": [{"title": "t", "body": "b", "files": ["f"]}],
             "alsoChanged": [["package-lock.json", "lock file"], ["docs/*.png"]]}
            """);
        guide!.AlsoChanged.ShouldBe([new WalkthroughAlsoChanged("package-lock.json", "lock file"), new WalkthroughAlsoChanged("docs/*.png", null)]);
    }

    [Fact]
    public void A_guide_that_isnt_an_object_says_what_it_should_be()
        => Parse("[]").Errors.ShouldBe(["\"guide\" must be an object: {\"summary\", \"chapters\": [...], ...}."]);

    private static (WalkthroughGuide? Guide, IReadOnlyList<string> Errors) Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return WalkthroughGuide.Parse(document.RootElement.Clone());
    }
}

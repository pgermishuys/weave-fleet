using WeaveFleet.Application.Services;

namespace WeaveFleet.Application.Tests.Services;

public sealed class GitHubLinkParserTests
{
    [Fact]
    public void Extract_finds_bare_and_markdown_urls()
    {
        var links = GitHubLinkParser.Extract(
            "Opened https://github.com/owner/repo/pull/187. Fixes [the issue](https://github.com/owner/repo/issues/42).");

        links.Select(l => l.Url).ShouldBe(
        [
            "https://github.com/owner/repo/pull/187",
            "https://github.com/owner/repo/issues/42",
        ], ignoreOrder: true);
    }

    [Fact]
    public void Extract_ignores_paths_and_fragments_after_the_number()
    {
        var link = GitHubLinkParser.Extract("see https://github.com/owner/repo/pull/187/files#discussion_r1").ShouldHaveSingleItem();

        link.Url.ShouldBe("https://github.com/owner/repo/pull/187");
        link.ResourceType.ShouldBe(GitHubLinkReference.PullRequest);
        link.ResourceId.ShouldBe("owner/repo#187");
    }

    [Fact]
    public void Extract_reads_shorthand_as_an_issue()
    {
        var link = GitHubLinkParser.Extract("Tracked in pgermishuys/weave-fleet#184.").ShouldHaveSingleItem();

        link.Owner.ShouldBe("pgermishuys");
        link.Repo.ShouldBe("weave-fleet");
        link.Number.ShouldBe(184);
        link.ResourceType.ShouldBe(GitHubLinkReference.Issue);
    }

    [Fact]
    public void Extract_prefers_the_pull_request_url_over_shorthand_for_the_same_number()
    {
        var link = GitHubLinkParser.Extract("owner/repo#9 is https://github.com/owner/repo/pull/9").ShouldHaveSingleItem();

        link.ResourceType.ShouldBe(GitHubLinkReference.PullRequest);
    }

    [Theory]
    [InlineData("Create a pull request by visiting https://github.com/owner/repo/pull/new/feat/x")]
    [InlineData("https://gitlab.com/owner/repo/-/merge_requests/3")]
    [InlineData("https://github.com/owner/repo/blob/main/src/file.ts#L12")]
    [InlineData("see https://example.com/owner/repo#12")]
    [InlineData("color: #123456")]
    public void Extract_ignores_things_that_are_not_pull_requests_or_issues(string text)
    {
        GitHubLinkParser.Extract(text).ShouldBeEmpty();
    }

    [Fact]
    public void Extract_skips_a_reference_at_the_end_of_text_that_is_still_streaming()
    {
        const string partial = "Opened https://github.com/owner/repo/pull/18";

        GitHubLinkParser.Extract(partial, textMayContinue: true).ShouldBeEmpty();
        GitHubLinkParser.Extract(partial + "7 for review", textMayContinue: true).ShouldHaveSingleItem().Number.ShouldBe(187);
        GitHubLinkParser.Extract(partial, textMayContinue: false).ShouldHaveSingleItem().Number.ShouldBe(18);
    }

    [Fact]
    public void TryParseUrl_requires_the_url_to_start_with_the_link()
    {
        GitHubLinkParser.TryParseUrl("https://github.com/owner/repo/issues/5", out var reference).ShouldBeTrue();
        reference.Url.ShouldBe("https://github.com/owner/repo/issues/5");

        GitHubLinkParser.TryParseUrl("prefix https://github.com/owner/repo/issues/5", out _).ShouldBeFalse();
        GitHubLinkParser.TryParseUrl("https://github.com/owner/repo", out _).ShouldBeFalse();
    }
}

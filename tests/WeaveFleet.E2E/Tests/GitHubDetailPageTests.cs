using System.Text.Json;
using Microsoft.Playwright;
using WeaveFleet.E2E.Infrastructure;
using WeaveFleet.E2E.Pages;

namespace WeaveFleet.E2E.Tests;

/// <summary>
/// Focused smoke tests for GitHub issue and pull request detail routes.
/// </summary>
[Trait("Category", "E2E")]
[Trait("Lane", "Smoke")]
public sealed class GitHubDetailPageTests : E2ETestBase,
    IClassFixture<FleetWebApplicationFactory>,
    IClassFixture<PlaywrightFixture>
{
    public GitHubDetailPageTests(FleetWebApplicationFactory factory, PlaywrightFixture playwright)
        : base(factory, playwright) { }

    [Fact]
    public async Task GitHubIssueDetailRouteRendersSeededTitleAndContent()
    {
        await WithFailureCapture(async () =>
        {
            const string owner = "acme";
            const string repo = "roadrunner";
            const int issueNumber = 42;
            const string title = "Smoke test issue title";
            const string body = "Seeded issue body content for detail route rendering.";

            await StubGitHubIssueAsync(owner, repo, issueNumber, title, body);

            var detailPage = new GitHubDetailPage(Page);
            await detailPage.GotoIssueAsync(owner, repo, issueNumber);

            Page.Url.ShouldContain($"/github/{owner}/{repo}/issues/{issueNumber}");
            (await detailPage.GetTitleAsync()).ShouldBe(title);

            var overviewText = await detailPage.GetOverviewTextAsync();
            overviewText.ShouldNotBeNull();
            overviewText.ShouldContain(body);
        });
    }

    [Fact]
    public async Task GitHubPullRequestDetailRouteRendersSeededTitleAndContent()
    {
        await WithFailureCapture(async () =>
        {
            const string owner = "acme";
            const string repo = "roadrunner";
            const int pullRequestNumber = 73;
            const string title = "Smoke test pull request title";
            const string body = "Seeded pull request body content for detail route rendering.";

            await StubGitHubPullRequestAsync(owner, repo, pullRequestNumber, title, body);

            var detailPage = new GitHubDetailPage(Page);
            await detailPage.GotoPullRequestAsync(owner, repo, pullRequestNumber);

            Page.Url.ShouldContain($"/github/{owner}/{repo}/pulls/{pullRequestNumber}");
            (await detailPage.GetTitleAsync()).ShouldBe(title);

            var overviewText = await detailPage.GetOverviewTextAsync();
            overviewText.ShouldNotBeNull();
            overviewText.ShouldContain(body);
        });
    }

    private Task StubGitHubIssueAsync(string owner, string repo, int issueNumber, string title, string body)
    {
        var issue = JsonSerializer.Serialize(new
        {
            id = 1001,
            number = issueNumber,
            title,
            body,
            html_url = $"https://github.com/{owner}/{repo}/issues/{issueNumber}",
            state = "open",
            labels = new[]
            {
                new
                {
                    name = "bug",
                    color = "d73a4a"
                }
            },
            user = new
            {
                login = "octocat",
                avatar_url = string.Empty
            },
            comments = 0,
            created_at = "2026-04-20T08:30:00Z",
            updated_at = "2026-04-20T09:00:00Z"
        });

        return Task.WhenAll(
            Page.RouteAsync($"**/api/integrations/github/repos/{owner}/{repo}/issues/{issueNumber}", route => FulfillJsonAsync(route, issue)),
            Page.RouteAsync($"**/api/integrations/github/repos/{owner}/{repo}/issues/{issueNumber}/comments", route => FulfillJsonAsync(route, "[]")));
    }

    private Task StubGitHubPullRequestAsync(string owner, string repo, int pullRequestNumber, string title, string body)
    {
        var pullRequest = JsonSerializer.Serialize(new
        {
            id = 2001,
            number = pullRequestNumber,
            title,
            body,
            html_url = $"https://github.com/{owner}/{repo}/pull/{pullRequestNumber}",
            state = "open",
            labels = new[]
            {
                new
                {
                    name = "enhancement",
                    color = "a2eeef"
                }
            },
            user = new
            {
                login = "octocat",
                avatar_url = string.Empty
            },
            comments = 0,
            additions = 12,
            deletions = 3,
            changed_files = 2,
            head = new
            {
                @ref = "feature/github-detail-smoke",
                sha = "abc123"
            },
            @base = new
            {
                @ref = "main",
                sha = "def456"
            },
            created_at = "2026-04-20T10:00:00Z",
            updated_at = "2026-04-20T10:30:00Z",
            merged_at = (string?)null,
            draft = false
        });

        return Task.WhenAll(
            Page.RouteAsync($"**/api/integrations/github/repos/{owner}/{repo}/pulls/{pullRequestNumber}", route => FulfillJsonAsync(route, pullRequest)),
            Page.RouteAsync($"**/api/integrations/github/repos/{owner}/{repo}/issues/{pullRequestNumber}/comments", route => FulfillJsonAsync(route, "[]")),
            Page.RouteAsync($"**/api/integrations/github/repos/{owner}/{repo}/pulls/{pullRequestNumber}/comments", route => FulfillJsonAsync(route, "[]")));
    }

    private static Task FulfillJsonAsync(IRoute route, string body)
        => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "application/json",
            Body = body
        });
}

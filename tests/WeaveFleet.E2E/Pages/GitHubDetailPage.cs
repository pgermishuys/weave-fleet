using Microsoft.Playwright;

namespace WeaveFleet.E2E.Pages;

/// <summary>
/// Page object for GitHub issue and pull request detail routes.
/// </summary>
public sealed class GitHubDetailPage(IPage page)
{
    private readonly IPage _page = page;

    private ILocator DetailShell => _page.Locator("article.detail-shell");
    private ILocator TitleHeading => _page.Locator("h1.detail-title");
    private ILocator OverviewPanel => _page.Locator("section[aria-label='Overview']");
    private ILocator OverviewHeading => OverviewPanel.GetByRole(AriaRole.Heading, new LocatorGetByRoleOptions { NameString = "Overview", Exact = true });
    private ILocator OverviewContent => OverviewPanel.Locator(".detail-markdown, .detail-panel__empty").First;

    /// <summary>Navigate directly to a GitHub issue detail route.</summary>
    public async Task GotoIssueAsync(string owner, string repo, int number)
    {
        await _page.GotoAsync($"/github/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/issues/{number}");
        await WaitForLoadedAsync();
    }

    /// <summary>Navigate directly to a GitHub pull request detail route.</summary>
    public async Task GotoPullRequestAsync(string owner, string repo, int number)
    {
        await _page.GotoAsync($"/github/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/pulls/{number}");
        await WaitForLoadedAsync();
    }

    /// <summary>Wait for the detail shell, title, and overview content to render.</summary>
    public async Task WaitForLoadedAsync()
    {
        await Assertions.Expect(DetailShell).ToBeVisibleAsync();
        await Assertions.Expect(TitleHeading).ToBeVisibleAsync();
        await Assertions.Expect(OverviewHeading).ToBeVisibleAsync();
        await Assertions.Expect(OverviewContent).ToBeVisibleAsync();
    }

    /// <summary>Read the visible page title.</summary>
    public Task<string?> GetTitleAsync()
        => TitleHeading.TextContentAsync();

    /// <summary>Read the rendered overview body text.</summary>
    public Task<string?> GetOverviewTextAsync()
        => OverviewContent.TextContentAsync();
}

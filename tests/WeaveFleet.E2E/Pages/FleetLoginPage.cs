using Microsoft.Playwright;

namespace WeaveFleet.E2E.Pages;

/// <summary>
/// Page object for the Fleet public login landing page (/login).
/// </summary>
public sealed class FleetLoginPage(IPage page)
{
    private readonly IPage _page = page;

    private ILocator SignInLink => _page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Sign in" });

    /// <summary>
    /// Waits for the Fleet login landing page to be visible.
    /// </summary>
    public async Task WaitForVisibleAsync()
    {
        var options = new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 };
        await Assertions.Expect(SignInLink).ToBeVisibleAsync(options);
    }

    /// <summary>
    /// Clicks the "Sign in" button to navigate to the backend OIDC challenge.
    /// </summary>
    public Task ClickSignInAsync()
        => SignInLink.ClickAsync();
}

using Microsoft.Playwright;

namespace WeaveFleet.E2E.Pages;

/// <summary>
/// Page object for the test Duende IdP login page.
/// </summary>
public sealed class IdpLoginPage(IPage page)
{
    private readonly IPage _page = page;

    private ILocator UsernameInput => _page.GetByTestId("login-username");
    private ILocator PasswordInput => _page.GetByTestId("login-password");
    private ILocator SubmitButton => _page.GetByTestId("login-submit");

    public async Task WaitForVisibleAsync()
    {
        // Use a generous timeout for the first login — the OIDC middleware's initial
        // backchannel discovery call to the IdP can be slow on CI cold starts.
        var options = new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 };
        await Assertions.Expect(UsernameInput).ToBeVisibleAsync(options);
        await Assertions.Expect(PasswordInput).ToBeVisibleAsync(options);
        await Assertions.Expect(SubmitButton).ToBeVisibleAsync(options);
    }

    public Task FillUsernameAsync(string username)
        => UsernameInput.FillAsync(username);

    public Task FillPasswordAsync(string password)
        => PasswordInput.FillAsync(password);

    public Task SubmitAsync()
        => SubmitButton.ClickAsync();

    public Task WaitForRedirectToFleetAsync(string serverUrl)
        => _page.WaitForURLAsync(
            url => url.StartsWith(serverUrl, StringComparison.OrdinalIgnoreCase)
                && !url.Contains("/connect/", StringComparison.OrdinalIgnoreCase),
            new PageWaitForURLOptions { Timeout = 15_000 });
}

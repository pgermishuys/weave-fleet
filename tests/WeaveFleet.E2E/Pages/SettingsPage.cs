using Microsoft.Playwright;

namespace WeaveFleet.E2E.Pages;

/// <summary>
/// Page object for the Settings page ("/settings").
/// Covers the workspace preferences and credentials workflows used by focused E2E tests.
/// </summary>
public sealed class SettingsPage(IPage page)
{
    private readonly IPage _page = page;

    private ILocator PageHeading => _page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { NameString = "Settings", Exact = true });
    private ILocator CredentialsHeading => _page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { NameString = "Credentials", Exact = true });
    private ILocator WorkspaceLabelInput => _page.GetByLabel("Workspace label");
    private ILocator AddApiKeyButton => _page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { NameString = "Add API Key" });
    private ILocator AddApiKeyFormHeading => _page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { NameString = "Add API key" });
    private ILocator AddApiKeyForm => _page.Locator("form").Filter(new LocatorFilterOptions { Has = _page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { NameString = "Save API Key" }) });
    private ILocator CredentialLabelInput => AddApiKeyForm.GetByLabel("Label");
    private ILocator CredentialProviderInput => AddApiKeyForm.GetByLabel("Provider");
    private ILocator CredentialTypeInput => AddApiKeyForm.GetByLabel("Type");
    private ILocator CredentialValueInput => AddApiKeyForm.GetByLabel("Secret value");
    private ILocator SaveApiKeyButton => AddApiKeyForm.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { NameString = "Save API Key" });

    /// <summary>Navigate to the settings page and wait for the main settings shell.</summary>
    public async Task GotoAsync()
    {
        await _page.GotoAsync("/settings");
        await WaitForLoadedAsync();
    }

    /// <summary>Wait for the page heading and primary settings controls to render.</summary>
    public async Task WaitForLoadedAsync()
    {
        await Assertions.Expect(PageHeading).ToBeVisibleAsync();
        await Assertions.Expect(CredentialsHeading).ToBeVisibleAsync();
        await Assertions.Expect(WorkspaceLabelInput).ToBeVisibleAsync();
    }

    /// <summary>Update the workspace display name preference.</summary>
    public Task SetWorkspaceLabelAsync(string value)
        => WorkspaceLabelInput.FillAsync(value);

    /// <summary>Read the current workspace display name preference.</summary>
    public Task<string> GetWorkspaceLabelAsync()
        => WorkspaceLabelInput.InputValueAsync();

    /// <summary>Open the credential add form.</summary>
    public async Task OpenAddApiKeyFormAsync()
    {
        await AddApiKeyButton.ClickAsync();
        await Assertions.Expect(AddApiKeyFormHeading).ToBeVisibleAsync();
    }

    /// <summary>Add a new API key credential through the settings form.</summary>
    public async Task AddApiKeyAsync(string label, string provider, string kind, string value)
    {
        await OpenAddApiKeyFormAsync();
        await CredentialLabelInput.FillAsync(label);
        await CredentialProviderInput.FillAsync(provider);
        await CredentialTypeInput.FillAsync(kind);
        await CredentialValueInput.FillAsync(value);
        await SaveApiKeyButton.ClickAsync();

        await Assertions.Expect(AddApiKeyFormHeading).ToBeHiddenAsync();
        await WaitForCredentialAsync(label);
    }

    /// <summary>Wait for a saved credential card to appear.</summary>
    public Task WaitForCredentialAsync(string label)
        => CredentialCard(label).WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

    /// <summary>Read the rendered text for a saved credential card.</summary>
    public Task<string?> GetCredentialCardTextAsync(string label)
        => CredentialCard(label).TextContentAsync();

    private ILocator CredentialCard(string label)
        => _page.Locator("article").Filter(new LocatorFilterOptions { HasText = label }).First;
}

using Microsoft.Playwright;

namespace WeaveFleet.E2E.Pages;

/// <summary>
/// Page object for the onboarding wizard dialog.
/// Uses text-based selectors because the onboarding components have no <c>data-testid</c> attributes.
/// </summary>
public sealed class OnboardingWizardPage(IPage page)
{
    private readonly IPage _page = page;

    // ── Wizard dialog ────────────────────────────────────────────────────────

    private ILocator WizardDialog => _page.Locator("[data-slot='dialog-content']");

    /// <summary>Wait for the onboarding wizard dialog to appear.</summary>
    public async Task WaitForVisibleAsync()
    {
        await Assertions.Expect(WizardDialog).ToBeVisibleAsync(
            new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
    }

    /// <summary>Assert that the onboarding wizard dialog is NOT visible.</summary>
    public async Task WaitForHiddenAsync()
    {
        await Assertions.Expect(WizardDialog).Not.ToBeVisibleAsync(
            new LocatorAssertionsToBeVisibleOptions { Timeout = 10_000 });
    }

    // ── Welcome step ─────────────────────────────────────────────────────────

    private ILocator WelcomeHeading => WizardDialog.GetByText("Welcome to Weave");
    private ILocator GetStartedButton => WizardDialog.GetByRole(AriaRole.Button, new() { Name = "Get Started" });

    /// <summary>Assert the Welcome step is visible.</summary>
    public async Task WaitForWelcomeStepAsync()
    {
        await Assertions.Expect(WelcomeHeading).ToBeVisibleAsync();
        await Assertions.Expect(GetStartedButton).ToBeVisibleAsync();
    }

    /// <summary>Click "Get Started" to advance from Welcome to Credential step.</summary>
    public async Task ClickGetStartedAsync()
    {
        await GetStartedButton.ClickAsync();
    }

    // ── Credential step ──────────────────────────────────────────────────────

    private ILocator CredentialHeading => WizardDialog.GetByText("Connect your API keys");
    private ILocator ApiKeyInput => WizardDialog.Locator("#onboard-api-key");
    private ILocator SaveApiKeyButton => WizardDialog.GetByRole(AriaRole.Button, new() { Name = "Save API Key" });
    private ILocator SkipForNowButton => WizardDialog.GetByRole(AriaRole.Button, new() { Name = "Skip for now" });
    private ILocator ContinueButton => WizardDialog.GetByRole(AriaRole.Button, new() { Name = "Continue" });
    private ILocator ApiKeySavedIndicator => WizardDialog.GetByText("API key saved.");

    /// <summary>Assert the Credential step is visible.</summary>
    public async Task WaitForCredentialStepAsync()
    {
        await Assertions.Expect(CredentialHeading).ToBeVisibleAsync();
    }

    /// <summary>Fill the API key input and click "Save API Key".</summary>
    public async Task SaveApiKeyAsync(string apiKey)
    {
        await ApiKeyInput.FillAsync(apiKey);
        await SaveApiKeyButton.ClickAsync();

        // Wait for the success indicator
        await Assertions.Expect(ApiKeySavedIndicator).ToBeVisibleAsync(
            new LocatorAssertionsToBeVisibleOptions { Timeout = 10_000 });
    }

    /// <summary>Click "Continue" after saving a credential.</summary>
    public async Task ClickContinueAsync()
    {
        await ContinueButton.ClickAsync();
    }

    /// <summary>Click "Skip for now" on the credential step.</summary>
    public async Task ClickSkipForNowAsync()
    {
        await SkipForNowButton.ClickAsync();
    }

    // ── Ready step ───────────────────────────────────────────────────────────

    private ILocator ReadyHeading => WizardDialog.GetByText("You're all set!");
    private ILocator StartSessionButton => WizardDialog.GetByRole(AriaRole.Button, new() { Name = "Start a Session" });

    /// <summary>Assert the Ready step is visible.</summary>
    public async Task WaitForReadyStepAsync()
    {
        await Assertions.Expect(ReadyHeading).ToBeVisibleAsync();
        await Assertions.Expect(StartSessionButton).ToBeVisibleAsync();
    }

    /// <summary>
    /// Click "Start a Session" to complete onboarding.
    /// This triggers <c>POST /api/user/me/complete-onboarding</c> and dismisses the wizard.
    /// </summary>
    public async Task ClickStartSessionAsync()
    {
        await StartSessionButton.ClickAsync();
    }

    // ── Composite helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Walk through the entire onboarding wizard: Welcome → Credential (skip) → Ready → Start.
    /// </summary>
    public async Task CompleteFullWizardAsync()
    {
        await WaitForWelcomeStepAsync();
        await ClickGetStartedAsync();

        await WaitForCredentialStepAsync();
        await ClickSkipForNowAsync();

        await WaitForReadyStepAsync();
        await ClickStartSessionAsync();
    }
}

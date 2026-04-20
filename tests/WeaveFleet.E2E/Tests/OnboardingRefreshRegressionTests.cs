using WeaveFleet.E2E.Infrastructure;
using WeaveFleet.E2E.Pages;

namespace WeaveFleet.E2E.Tests;

/// <summary>
/// Regression tests for browser refresh during and after the onboarding wizard.
/// These verify that the <c>OnboardingGate</c> correctly re-evaluates server state
/// (<c>onboardingStatus.completed</c> from <c>GET /api/user/me</c>) after a full
/// page reload, and that no intermediate state causes the wizard to break.
/// </summary>
#pragma warning disable CA1001 // Types that own disposable fields should implement IDisposable — disposal handled by IAsyncLifetime
[Trait("Category", "E2E")]
[Trait("Category", "AuthE2E")]
[Trait("Lane", "Regression")]
public sealed class OnboardingRefreshRegressionTests : AuthE2ETestBase,
    IClassFixture<AuthFleetWebApplicationFactory>,
    IClassFixture<PlaywrightFixture>
#pragma warning restore CA1001
{
    public OnboardingRefreshRegressionTests(AuthFleetWebApplicationFactory factory, PlaywrightFixture playwright)
        : base(factory, playwright) { }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        // Reset onboarding so wizard tests are not affected by cross-test state
        await ResetUserOnboardingAsync("new@example.com");
    }

    /// <summary>
    /// Refresh mid-wizard: sign in → wizard appears at Welcome → advance to Credential step →
    /// <c>Page.ReloadAsync()</c> → wizard reappears (because <c>complete-onboarding</c> hasn't been
    /// POSTed yet) → user can continue through wizard → complete → wizard dismisses.
    /// </summary>
    [Fact]
    public async Task RefreshMidWizard_WizardReappearsAndCanBeCompleted()
    {
        await WithFailureCapture(async () =>
        {
            await LoginAsync("newuser", "password");

            var wizard = new OnboardingWizardPage(Page);
            await wizard.WaitForVisibleAsync();
            await wizard.WaitForWelcomeStepAsync();

            // Advance to Credential step
            await wizard.ClickGetStartedAsync();
            await wizard.WaitForCredentialStepAsync();

            // Browser refresh — simulates F5 mid-wizard
            await Page.ReloadAsync(new Microsoft.Playwright.PageReloadOptions { WaitUntil = WaitUntilState.NetworkIdle });

            // The SPA re-evaluates OnboardingGate: completed is still false → wizard reappears
            wizard = new OnboardingWizardPage(Page);
            await wizard.WaitForVisibleAsync();
            // After reload, wizard restarts at Welcome (React state is lost)
            await wizard.WaitForWelcomeStepAsync();

            // Complete the wizard from the beginning
            await wizard.CompleteFullWizardAsync();
            await wizard.WaitForHiddenAsync();
        });
    }

    /// <summary>
    /// Refresh after completion: complete the wizard → <c>Page.ReloadAsync()</c> →
    /// wizard does NOT reappear (server state has <c>onboarding_completed_at</c> set).
    /// </summary>
    [Fact]
    public async Task RefreshAfterCompletion_WizardDoesNotReappear()
    {
        await WithFailureCapture(async () =>
        {
            await LoginAsync("newuser", "password");

            var wizard = new OnboardingWizardPage(Page);
            await wizard.WaitForVisibleAsync();
            await wizard.CompleteFullWizardAsync();
            await wizard.WaitForHiddenAsync();

            // Browser refresh — simulates F5 after onboarding is done
            await Page.ReloadAsync(new Microsoft.Playwright.PageReloadOptions { WaitUntil = WaitUntilState.NetworkIdle });

            await Microsoft.Playwright.Assertions.Expect(Page.GetByTestId("summary-bar")).ToBeVisibleAsync();

            // Wizard should NOT reappear once the dashboard shell is rendered
            wizard = new OnboardingWizardPage(Page);
            await wizard.WaitForHiddenAsync();
        });
    }

    /// <summary>
    /// Refresh on Welcome step: wizard at Welcome → <c>Page.ReloadAsync()</c> →
    /// wizard reappears at Welcome (not at a broken state).
    /// </summary>
    [Fact]
    public async Task RefreshOnWelcomeStep_WizardReappearsAtWelcome()
    {
        await WithFailureCapture(async () =>
        {
            await LoginAsync("newuser", "password");

            var wizard = new OnboardingWizardPage(Page);
            await wizard.WaitForVisibleAsync();
            await wizard.WaitForWelcomeStepAsync();

            // Browser refresh on the Welcome step
            await Page.ReloadAsync(new Microsoft.Playwright.PageReloadOptions { WaitUntil = WaitUntilState.NetworkIdle });

            // Wizard should reappear at Welcome (React state is lost, server says not completed)
            wizard = new OnboardingWizardPage(Page);
            await wizard.WaitForVisibleAsync();
            await wizard.WaitForWelcomeStepAsync();
        });
    }
}

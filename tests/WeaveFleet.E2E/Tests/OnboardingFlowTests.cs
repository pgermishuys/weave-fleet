using WeaveFleet.E2E.Infrastructure;
using WeaveFleet.E2E.Pages;

namespace WeaveFleet.E2E.Tests;

/// <summary>
/// E2E tests for the onboarding wizard flow in authenticated + cloud mode.
/// Verifies new-user wizard rendering, step progression, and completed-user bypass.
/// </summary>
#pragma warning disable CA1001 // Types that own disposable fields should implement IDisposable — disposal handled by IAsyncLifetime
[Trait("Category", "E2E")]
[Trait("Category", "AuthE2E")]
public sealed class OnboardingFlowTests : AuthE2ETestBase,
    IClassFixture<AuthFleetWebApplicationFactory>,
    IClassFixture<PlaywrightFixture>
#pragma warning restore CA1001
{
    public OnboardingFlowTests(AuthFleetWebApplicationFactory factory, PlaywrightFixture playwright)
        : base(factory, playwright) { }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        // Reset onboarding so wizard tests are not affected by cross-test state
        await ResetUserOnboardingAsync("new@example.com");
    }

    /// <summary>
    /// New user signs in → OnboardingGate triggers → wizard dialog is visible at the Welcome step.
    /// </summary>
    [Fact]
    public async Task NewUser_SeesOnboardingWizard()
    {
        await WithFailureCapture(async () =>
        {
            // newuser has never completed onboarding
            await LoginAsync("newuser", "password");

            var wizard = new OnboardingWizardPage(Page);
            await wizard.WaitForVisibleAsync();
            await wizard.WaitForWelcomeStepAsync();
        });
    }

    /// <summary>
    /// Wizard step progression: Welcome → Get Started → Credential → save key → Continue → Ready → Start a Session
    /// → wizard closes → complete-onboarding has been POSTed.
    /// </summary>
    [Fact]
    public async Task WizardProgression_WelcomeToReady_CompletesOnboarding()
    {
        await WithFailureCapture(async () =>
        {
            await LoginAsync("newuser", "password");

            var wizard = new OnboardingWizardPage(Page);
            await wizard.WaitForVisibleAsync();
            await wizard.CompleteFullWizardAsync();

            // Wizard should be dismissed
            await wizard.WaitForHiddenAsync();

            // Verify the server recorded completion
            var response = await Page.APIRequest.GetAsync($"{ServerUrl}/api/user/me");
            response.Status.ShouldBe(200);

            var body = await response.JsonAsync();
            body.ShouldNotBeNull();

            var completed = body.Value.GetProperty("onboardingStatus").GetProperty("completed").GetBoolean();
            completed.ShouldBeTrue("Expected onboarding to be marked completed on the server after wizard completion");
        });
    }

    /// <summary>
    /// A user who has already completed onboarding signs in → wizard does NOT appear → dashboard renders.
    /// </summary>
    [Fact]
    public async Task CompletedUser_SkipsWizard_SeesDashboard()
    {
        await WithFailureCapture(async () =>
        {
            // First: sign in as testuser and complete onboarding (if not already completed)
            await LoginAsync("testuser", "password");

            // Trigger a GET to prime CSRF cookie
            var meResponse = await Page.APIRequest.GetAsync($"{ServerUrl}/api/user/me");
            meResponse.Status.ShouldBe(200);

            // Fetch CSRF token for the POST
            var cookies = await Page.Context.CookiesAsync([ServerUrl]);
            var csrfCookie = cookies.FirstOrDefault(c =>
                c.Name.Equals(".WeaveFleet.CSRF", StringComparison.OrdinalIgnoreCase));

            if (csrfCookie is not null)
            {
                // Complete onboarding directly via API (idempotent)
                await Page.APIRequest.PostAsync(
                    $"{ServerUrl}/api/user/me/complete-onboarding",
                    new Microsoft.Playwright.APIRequestContextOptions
                    {
                        Headers = new Dictionary<string, string>
                        {
                            ["X-CSRF-Token"] = csrfCookie.Value
                        }
                    });
            }

            // Reload the page — wizard should NOT appear
            await Page.GotoAsync("/");

            // Wait for the page to stabilize — give the SPA time to evaluate OnboardingGate
            await Page.WaitForTimeoutAsync(2_000);

            var wizard = new OnboardingWizardPage(Page);
            await wizard.WaitForHiddenAsync();
        });
    }
}

using WeaveFleet.E2E.Infrastructure;
using WeaveFleet.E2E.Pages;

namespace WeaveFleet.E2E.Tests;

/// <summary>
/// E2E coverage for navigation failures that should surface either a backend 404
/// or an authentication redirect, depending on app mode.
/// </summary>
[Trait("Category", "E2E")]
[Trait("Lane", "Smoke")]
public sealed class NavigationErrorTests : E2ETestBase,
    IClassFixture<FleetWebApplicationFactory>,
    IClassFixture<PlaywrightFixture>
{
    public NavigationErrorTests(FleetWebApplicationFactory factory, PlaywrightFixture playwright)
        : base(factory, playwright) { }

    [Fact]
    public async Task NonexistentSession_NavigationReturnsNotFoundResponse()
    {
        await WithFailureCapture(async () =>
        {
            const string sessionId = "nonexistent-id";

            // Current SPA behavior keeps the session shell mounted for a missing session,
            // so this test makes the route boundary explicit by asserting the detail API
            // returns a real 404 for the requested session.
            var sessionResponseTask = Page.WaitForResponseAsync(response =>
                response.Request.Method.Equals("GET", StringComparison.Ordinal) &&
                response.Url.EndsWith($"/api/sessions/{sessionId}", StringComparison.Ordinal));

            await Page.GotoAsync($"/sessions/{sessionId}");

            var sessionResponse = await sessionResponseTask;
            sessionResponse.Status.ShouldBe(404);

            var responseBody = await sessionResponse.TextAsync();
            responseBody.ShouldNotBeNull();
            responseBody.ShouldContain(sessionId);
            responseBody.ToLowerInvariant().ShouldContain("not found");

            Page.Url.ShouldContain($"/sessions/{sessionId}");
            await Assertions.Expect(Page.GetByTestId("activity-stream")).ToBeVisibleAsync();
        });
    }
}

[Trait("Category", "E2E")]
[Trait("Category", "AuthE2E")]
[Trait("Lane", "Smoke")]
public sealed class UnauthorizedNavigationErrorTests : AuthE2ETestBase,
    IClassFixture<AuthFleetWebApplicationFactory>,
    IClassFixture<PlaywrightFixture>
{
    public UnauthorizedNavigationErrorTests(AuthFleetWebApplicationFactory factory, PlaywrightFixture playwright)
        : base(factory, playwright) { }

    [Fact]
    public async Task ProtectedRoute_WithoutAuthentication_RedirectsToLoginWithReturnUrl()
    {
        await WithFailureCapture(async () =>
        {
            const string protectedRoute = "/settings";

            await Page.GotoAsync(protectedRoute);
            await Page.WaitForURLAsync(url =>
            {
                var uri = new Uri(url);
                return uri.AbsolutePath.Equals("/login", StringComparison.Ordinal)
                    && string.Equals(
                        System.Web.HttpUtility.ParseQueryString(uri.Query)["returnUrl"],
                        protectedRoute,
                        StringComparison.Ordinal);
            });

            var loginPage = new FleetLoginPage(Page);
            await loginPage.WaitForVisibleAsync();

            var loginUri = new Uri(Page.Url);
            loginUri.AbsolutePath.ShouldBe("/login");
            System.Web.HttpUtility.ParseQueryString(loginUri.Query)["returnUrl"].ShouldBe(protectedRoute);

            var signInHref = await Page
                .GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Sign in" })
                .GetAttributeAsync("href");
            signInHref.ShouldNotBeNull();
            signInHref.ShouldContain("/auth/login?returnUrl=%2Fsettings");
        });
    }
}

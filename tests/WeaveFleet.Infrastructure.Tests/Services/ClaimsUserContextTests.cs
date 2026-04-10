using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.Infrastructure.Tests.Services;

public sealed class ClaimsUserContextTests
{
    [Fact]
    public void ClaimsUserContext_ReadsMappedOidcClaims()
    {
        var httpContextAccessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("sub", "user-123"),
                    new Claim("email", "user@example.com"),
                    new Claim("name", "Test User")
                ],
                authenticationType: "Test"))
            }
        };

        var context = new ClaimsUserContext(httpContextAccessor);

        context.UserId.ShouldBe("user-123");
        context.Email.ShouldBe("user@example.com");
        context.DisplayName.ShouldBe("Test User");
        context.IsAuthenticated.ShouldBeTrue();
    }

    [Fact]
    public void ClaimsUserContext_ThrowsWhenSubClaimIsMissing()
    {
        var httpContextAccessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(authenticationType: "Test"))
            }
        };

        var context = new ClaimsUserContext(httpContextAccessor);

        var exception = Should.Throw<InvalidOperationException>(() => _ = context.UserId);
        exception.Message.ShouldContain("sub");
    }
}

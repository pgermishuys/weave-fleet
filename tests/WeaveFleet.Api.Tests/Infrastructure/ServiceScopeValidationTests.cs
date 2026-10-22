using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace WeaveFleet.Api.Tests.Infrastructure;

public sealed class ServiceScopeValidationTests
{
    // Development validates scopes; a singleton holding a scoped service refuses to start there and is a silent captive in Production.
    [Fact]
    public void The_real_container_builds_with_scope_validation_on()
    {
        using var factory = new ApiWebApplicationFactory(authEnabled: false)
            .WithWebHostBuilder(b => b.UseDefaultServiceProvider(o => { o.ValidateScopes = true; o.ValidateOnBuild = true; }));

        Should.NotThrow(() => factory.CreateClient());
    }
}

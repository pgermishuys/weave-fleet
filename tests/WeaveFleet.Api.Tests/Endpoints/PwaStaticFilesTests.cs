using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using WeaveFleet.Api.Tests.Auth;
using WeaveFleet.Api.Tests.Infrastructure;

namespace WeaveFleet.Api.Tests.Endpoints;

/// <summary>
/// What makes Fleet installable: the manifest with its own content type, and the service worker never cached
/// stale. Both are served to anyone, since a phone fetches them before it has signed in.
/// </summary>
[Collection(LocalTokenAuthServiceTestsGroup.Name)]
public sealed class PwaStaticFilesTests
{
    [Fact]
    public async Task The_manifest_is_served_as_a_web_app_manifest_without_signing_in()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        await File.WriteAllTextAsync(Path.Combine(factory.WebRootPath, "manifest.webmanifest"), """{"name":"Weave Fleet"}""");

        var response = await client.GetAsync("/manifest.webmanifest");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/manifest+json");
        response.Headers.CacheControl?.NoCache.ShouldBeTrue();
    }

    [Fact]
    public async Task The_service_worker_is_never_cached_and_may_control_the_whole_origin()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        await File.WriteAllTextAsync(Path.Combine(factory.WebRootPath, "sw.js"), "self.addEventListener('push', () => {});");

        var response = await client.GetAsync("/sw.js");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("text/javascript");
        response.Headers.CacheControl?.NoCache.ShouldBeTrue();
        response.Headers.GetValues("Service-Worker-Allowed").Single().ShouldBe("/");
    }

    [Fact]
    public async Task Icons_are_served_without_signing_in()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        Directory.CreateDirectory(Path.Combine(factory.WebRootPath, "icons"));
        await File.WriteAllBytesAsync(Path.Combine(factory.WebRootPath, "icons", "icon-192.png"), [0x89, 0x50, 0x4E, 0x47]);

        var response = await client.GetAsync("/icons/icon-192.png");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("image/png");
    }

    // A reachable bind needs the token for everything the app's API serves, so these pass only as static files.
    private static ApiWebApplicationFactory CreateFactory()
        => new(authEnabled: false, tokenAuthEnabled: true, simulateLocalhostRequest: true, host: "0.0.0.0");
}

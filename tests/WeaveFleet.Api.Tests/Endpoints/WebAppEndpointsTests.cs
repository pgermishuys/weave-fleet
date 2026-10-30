using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using WeaveFleet.Api.Endpoints;

namespace WeaveFleet.Api.Tests.Endpoints;

/// <summary>
/// What the browser (and the desktop app, which loads "/") may cache. fleet-reports #35: after an update the desktop
/// app showed the old UI until Ctrl+R, because "/" and deep links came from the SPA fallback without Cache-Control.
/// </summary>
public sealed class WebAppEndpointsTests : IAsyncLifetime
{
    private readonly string _webRoot = Path.Combine(Path.GetTempPath(), "fleet-webapp-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_webRoot, "assets"));
        await File.WriteAllTextAsync(Path.Combine(_webRoot, "index.html"), "<script src=\"/assets/index-abc123.js\"></script>");
        await File.WriteAllTextAsync(Path.Combine(_webRoot, "assets", "index-abc123.js"), "console.log(1)");
        await File.WriteAllTextAsync(Path.Combine(_webRoot, "sw.js"), "");
        await File.WriteAllTextAsync(Path.Combine(_webRoot, "manifest.webmanifest"), "{}");

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { WebRootPath = _webRoot });
        builder.WebHost.UseTestServer();
        _app = builder.Build();
        _app.MapGet("/api/ping", () => "pong");
        _app.UseFleetWebApp();
        await _app.StartAsync();
        _http = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
        Directory.Delete(_webRoot, recursive: true);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/settings")]
    [InlineData("/sessions/abc")]
    public async Task Index_html_is_always_revalidated(string path)
    {
        var response = await _http.GetAsync(path);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldContain("/assets/index-abc123.js");
        response.Headers.GetValues("Cache-Control").Single().ShouldBe("no-cache", $"{path} must be revalidated");
    }

    [Fact]
    public async Task Hashed_assets_are_cached_for_good()
    {
        var response = await _http.GetAsync("/assets/index-abc123.js");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues("Cache-Control").Single().ShouldBe("public, max-age=31536000, immutable");
    }

    [Fact]
    public async Task Service_worker_and_manifest_are_revalidated()
    {
        var worker = await _http.GetAsync("/sw.js");
        worker.Headers.GetValues("Cache-Control").Single().ShouldBe("no-cache");
        worker.Headers.GetValues("Service-Worker-Allowed").Single().ShouldBe("/");

        var manifest = await _http.GetAsync("/manifest.webmanifest");
        manifest.Headers.GetValues("Cache-Control").Single().ShouldBe("no-cache");
        manifest.Content.Headers.ContentType!.MediaType.ShouldBe("application/manifest+json");
    }

    [Fact]
    public async Task Api_routes_are_not_the_web_app()
    {
        (await _http.GetStringAsync("/api/ping")).ShouldBe("pong");
    }
}

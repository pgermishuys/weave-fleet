using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Endpoints;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Pages;

namespace WeaveFleet.Api.Tests.Endpoints;

/// <summary>
/// The pages agents show, as the canvas frames them. Sign-in is on and the client has none: the page's address is
/// what it takes to open it, while the API still turns the same client away.
/// </summary>
#pragma warning disable CA1001 // Type owns disposable fields — disposal handled by IAsyncLifetime.DisposeAsync
public sealed class PageEndpointTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private readonly DirectoryInfo _source = Directory.CreateTempSubdirectory("fleet-page-endpoint-");
    private ApiWebApplicationFactory? _factory;
    private HttpClient? _client;
    private string _pageId = "";

    public async Task InitializeAsync()
    {
        _factory = new ApiWebApplicationFactory(authEnabled: true);
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        Directory.CreateDirectory(Path.Combine(_source.FullName, "css"));
        await File.WriteAllTextAsync(Path.Combine(_source.FullName, "options.html"), "<link rel=\"stylesheet\" href=\"css/site.css\"><p>Options</p>");
        await File.WriteAllTextAsync(Path.Combine(_source.FullName, "css", "site.css"), "p { color: teal }");

        _pageId = PageIds.New();
        var copied = await _factory.Services.GetRequiredService<IPageStore>()
            .CopyAsync("sess-1", _pageId, Path.Combine(_source.FullName, "options.html"));
        copied.Problem.ShouldBeNull();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
            await _factory.DisposeAsync();
        _source.Delete(recursive: true);
    }

    [Fact]
    public async Task A_page_opens_without_signing_in_and_runs_sandboxed()
    {
        var response = await _client!.GetAsync($"/pages/{_pageId}/options.html");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");
        (await response.Content.ReadAsStringAsync()).ShouldContain("<p>Options</p>");
        response.Headers.GetValues("Content-Security-Policy").ShouldHaveSingleItem().ShouldBe(PageEndpoints.Sandbox);
        response.Headers.GetValues("Content-Security-Policy").Single().ShouldNotContain("allow-same-origin");
        response.Headers.GetValues("X-Content-Type-Options").ShouldHaveSingleItem().ShouldBe("nosniff");
        response.Headers.GetValues("Referrer-Policy").ShouldHaveSingleItem().ShouldBe("no-referrer");
        response.Headers.CacheControl!.NoCache.ShouldBeTrue();
        response.Headers.Contains("Set-Cookie").ShouldBeFalse();

        (await _client.GetAsync("/api/sessions")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_page_s_own_files_load_with_their_types()
    {
        var response = await _client!.GetAsync($"/pages/{_pageId}/css/site.css");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/css");
        response.Headers.GetValues("Content-Security-Policy").ShouldHaveSingleItem().ShouldBe(PageEndpoints.Sandbox);
    }

    [Fact]
    public async Task The_page_s_folder_serves_the_page_and_its_bare_address_gets_the_slash()
    {
        (await (await _client!.GetAsync($"/pages/{_pageId}/")).Content.ReadAsStringAsync()).ShouldContain("<p>Options</p>");

        var bare = await _client.GetAsync($"/pages/{_pageId}");
        bare.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        bare.Headers.Location!.OriginalString.ShouldBe($"/pages/{_pageId}/");
    }

    [Theory]
    [InlineData("/pages/{page}/..%2F..%2Ffleet.db")]
    [InlineData("/pages/{page}/.entry")]
    [InlineData("/pages/{page}/missing.html")]
    [InlineData("/pages/pg_00000000000000000000000000000000/options.html")]
    [InlineData("/pages/not-a-page/options.html")]
    public async Task Anything_that_is_not_a_file_of_a_page_is_not_found(string path)
    {
        var response = await _client!.GetAsync(path.Replace("{page}", _pageId, StringComparison.Ordinal));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}

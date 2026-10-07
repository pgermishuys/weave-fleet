using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Services;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.Api.Tests.Endpoints;

public sealed class UpdateEndpointTests
{
    [Fact]
    public async Task get_update_status_returns_ok_with_current_version()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: false);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/update/status");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<UpdateStatusDto>(JsonSerializerOptions.Web);
        body.ShouldNotBeNull();
        body.CurrentVersion.ShouldNotBeNullOrEmpty();
        body.Status.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task get_update_status_reflects_seeded_state()
    {
        await using var factory = new ApiWebApplicationFactory(
            authEnabled: false,
            configureTestServices: services =>
            {
                // Pre-seed a known state so we can assert what the endpoint returns.
                var holder = new UpdateStateHolder();
                holder.SetState(new UpdateState(
                    UpdateStatus.Available,
                    LatestVersion: "99.0.0",
                    DownloadUrl: "https://example.com/fleet.zip",
                    AssetName: "fleet-v99.0.0-linux-x64.tar.gz",
                    CheckedAt: DateTimeOffset.UtcNow,
                    Error: null));

                services.AddSingleton(holder);
            });
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/update/status");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<UpdateStatusDto>(JsonSerializerOptions.Web);
        body.ShouldNotBeNull();
        body.Status.ShouldBe("available");
        body.LatestVersion.ShouldBe("99.0.0");
    }

    [Fact]
    public async Task post_update_check_returns_accepted()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: false);
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/update/check", content: null);

        // The endpoint triggers an async check; it returns Accepted even if the check
        // finds no update (e.g. because we're in a dev layout during tests).
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task post_update_download_returns_bad_request_when_no_update_available()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: false);
        using var client = factory.CreateClient();

        // Default state is Unknown — not Available — so download should be rejected.
        var response = await client.PostAsync("/api/update/download", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task get_releases_returns_the_recent_releases_newest_first()
    {
        // Its own data folder: test servers keep their databases side by side in the temp folder, and a saved
        // release-notes.json there (from post_update_check, which asks the real GitHub) would be read instead.
        var dataDir = Directory.CreateTempSubdirectory("fleet-release-notes-").FullName;
        await using var factory = new ApiWebApplicationFactory(
            authEnabled: false,
            configureTestServices: services =>
            {
                // GitHub's list, answered locally: a pre-release and two published releases.
                services.AddSingleton(_ => new ReleaseNotesStore(
                    new StubHttpClientFactory("""
                        [
                          { "tag_name": "v0.47.0", "html_url": "https://example.test/v0.47.0", "body": "Next", "prerelease": true },
                          { "tag_name": "v0.45.0", "html_url": "https://example.test/v0.45.0", "body": "Pinned sessions", "published_at": "2026-10-06T08:00:00Z" },
                          { "tag_name": "v0.46.0", "html_url": "https://example.test/v0.46.0", "body": "Plan pages", "published_at": "2026-10-07T08:00:00Z" }
                        ]
                        """),
                    new WeaveFleet.Application.Configuration.FleetOptions { DatabasePath = Path.Combine(dataDir, "fleet.db") },
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<ReleaseNotesStore>.Instance));
            });
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/update/releases");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ReleaseNotesDto>(JsonSerializerOptions.Web);
        body.ShouldNotBeNull();
        body.Error.ShouldBeNull();
        body.FetchedAt.ShouldNotBeNull();
        body.Releases.Select(r => r.Version).ShouldBe(["0.46.0", "0.45.0"]);
        body.Releases[0].ShouldBe(new ReleaseNoteDto("0.46.0", "2026-10-07T08:00:00Z", "Plan pages", "https://example.test/v0.46.0"));
        Directory.Delete(dataDir, recursive: true);
    }

    private sealed class StubHttpClientFactory(string json) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler(json));

        private sealed class StubHandler(string json) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }

    private sealed record ReleaseNotesDto(IReadOnlyList<ReleaseNoteDto> Releases, string? FetchedAt, string? Error);

    private sealed record ReleaseNoteDto(string Version, string? PublishedAt, string Body, string Url);

    private sealed record UpdateStatusDto(
        string CurrentVersion,
        string Status,
        string? LatestVersion,
        string? CheckedAt,
        string? Error);
}

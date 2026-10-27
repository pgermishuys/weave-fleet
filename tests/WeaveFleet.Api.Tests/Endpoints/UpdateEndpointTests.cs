using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Updates;
using WeaveFleet.Infrastructure.Updates;

namespace WeaveFleet.Api.Tests.Endpoints;

public sealed class UpdateEndpointTests : IClassFixture<UpdateEndpointTests.Server>, IClassFixture<UpdateEndpointTests.SeededServer>
{
    private readonly ApiWebApplicationFactory _factory;
    private readonly UpdateStateHolder _holder;
    private readonly ApiWebApplicationFactory _seededFactory;

    public UpdateEndpointTests(Server server, SeededServer seeded)
    {
        _factory = server.Factory;
        _seededFactory = seeded.Factory;
        _holder = seeded.Holder;
    }

    public sealed class Server : IDisposable
    {
        public ApiWebApplicationFactory Factory { get; } = new(authEnabled: false);
        public void Dispose() => Factory.Dispose();
    }

    /// <summary>A server whose update state a test sets before it asks.</summary>
    public sealed class SeededServer : IDisposable
    {
        public UpdateStateHolder Holder { get; } = new();
        public ApiWebApplicationFactory Factory { get; }

        public SeededServer() => Factory = new(authEnabled: false, configureTestServices: services => services.AddSingleton(Holder));
        public void Dispose() => Factory.Dispose();
    }

    [Fact]
    public async Task get_update_status_returns_ok_with_current_version()
    {
        using var client = _factory.CreateClient();

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
        // Pre-seed a known state so we can assert what the endpoint returns.
        _holder.SetState(new UpdateState(
            UpdateStatus.Available,
            LatestVersion: "99.0.0",
            DownloadUrl: "https://example.com/fleet.zip",
            AssetName: "fleet-v99.0.0-linux-x64.tar.gz",
            CheckedAt: DateTimeOffset.UtcNow,
            Error: null));
        using var client = _seededFactory.CreateClient();

        var response = await client.GetAsync("/api/update/status");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<UpdateStatusDto>(JsonSerializerOptions.Web);
        body.ShouldNotBeNull();
        body.Status.ShouldBe("available");
        body.LatestVersion.ShouldBe("99.0.0");
    }

    [Fact]
    public async Task get_machine_says_where_the_fleet_is_with_updates()
    {
        _holder.SetState(new UpdateState(
            UpdateStatus.Staged,
            LatestVersion: "99.0.0",
            DownloadUrl: null,
            AssetName: null,
            CheckedAt: new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero),
            Error: null));
        using var client = _seededFactory.CreateClient();

        var machine = await client.GetFromJsonAsync<JsonElement>("/api/machine");

        var update = machine.GetProperty("update");
        update.GetProperty("status").GetString().ShouldBe("staged");
        update.GetProperty("latestVersion").GetString().ShouldBe("99.0.0");
        update.GetProperty("checkedAt").GetString().ShouldStartWith("2026-10-20T08:00:00");
        // Nothing is staged on disk in a test run.
        update.GetProperty("stagedVersion").ValueKind.ShouldBe(JsonValueKind.Null);
        // The status words are the ones /api/update/status uses.
        var status = await client.GetFromJsonAsync<UpdateStatusDto>("/api/update/status", JsonSerializerOptions.Web);
        status!.Status.ShouldBe(update.GetProperty("status").GetString());
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
    public async Task a_test_server_never_asks_github_for_an_update()
    {
        // Asking the real GitHub found a release newer than the build and downloaded it, 60 MB, into the test run.
        await using var factory = new ApiWebApplicationFactory(authEnabled: false);
        using var client = factory.CreateClient();

        (await client.PostAsync("/api/update/check", content: null)).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var status = await client.GetFromJsonAsync<UpdateStatusDto>("/api/update/status", JsonSerializerOptions.Web);
        status!.Status.ShouldBe("error");
        status.Error.ShouldNotBeNull().ShouldContain(ApiWebApplicationFactory.NoGitHub);
    }

    [Fact]
    public async Task each_test_server_stages_updates_in_its_own_folder()
    {
        // They used to share one next to the test assembly, so an update one of them staged showed on every other.
        await using var staging = new ApiWebApplicationFactory(authEnabled: false);
        await using var other = new ApiWebApplicationFactory(authEnabled: false);
        var dir = staging.Services.GetRequiredService<UpdateDownloadService>().StagingDirectory;
        Directory.CreateDirectory(dir);
        var manifest = Path.Combine(dir, "update-manifest.json");
        await File.WriteAllTextAsync(manifest, """{ "version": "99.0.0", "assetFileName": "fleet.tar.gz", "sha256": "00", "downloadedAt": "2026-10-27T08:00:00Z" }""");
        try
        {
            using var stagingClient = staging.CreateClient();
            using var otherClient = other.CreateClient();

            var staged = await stagingClient.GetFromJsonAsync<JsonElement>("/api/machine");
            var unstaged = await otherClient.GetFromJsonAsync<JsonElement>("/api/machine");

            staged.GetProperty("update").GetProperty("stagedVersion").GetString().ShouldBe("99.0.0");
            unstaged.GetProperty("update").GetProperty("stagedVersion").ValueKind.ShouldBe(JsonValueKind.Null);
        }
        finally
        {
            File.Delete(manifest);
        }
    }

    [Fact]
    public async Task post_update_download_returns_bad_request_when_no_update_available()
    {
        using var client = _factory.CreateClient();

        // Default state is Unknown — not Available — so download should be rejected.
        var response = await client.PostAsync("/api/update/download", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task get_releases_returns_the_recent_releases_newest_first()
    {
        // Its own data folder: test servers keep their databases side by side in the temp folder, and a saved
        // release-notes.json there would be read instead.
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

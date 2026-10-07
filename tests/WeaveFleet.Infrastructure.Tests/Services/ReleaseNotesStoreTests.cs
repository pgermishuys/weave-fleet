using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.Infrastructure.Tests.Services;

public sealed class ReleaseNotesStoreTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"fleet-release-notes-{Guid.NewGuid():N}");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero));
    private readonly FakeGitHub _github = new();

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
            Directory.Delete(_dataDir, recursive: true);
    }

    [Fact]
    public async Task With_nothing_saved_it_fetches_the_published_releases_newest_first()
    {
        _github.Releases =
        [
            Release("v0.45.0", "## What's Changed\n* Pinned sessions"),
            Release("v0.99.0", "Next", prerelease: true),
            Release("v0.46.0", "## What's Changed\n* Plan pages"),
            Release("v0.98.0", "Draft", draft: true),
        ];
        using var store = CreateStore();

        var notes = await store.GetAsync(CancellationToken.None);

        notes.Releases.Select(r => r.Version).ShouldBe(["0.46.0", "0.45.0"]);
        notes.Releases[0].Body.ShouldContain("Plan pages");
        notes.Releases[0].Url.ShouldBe("https://github.com/acme/fleet-releases/releases/tag/v0.46.0");
        notes.FetchedAt.ShouldBe(_time.GetUtcNow());
        notes.Error.ShouldBeNull();
        _github.Requests.ShouldBe(1);
        _github.LastPathAndQuery.ShouldBe("/repos/acme/fleet-releases/releases?per_page=20");
    }

    [Fact]
    public async Task Saved_notes_are_read_from_the_data_folder_without_asking_GitHub()
    {
        _github.Releases = [Release("v0.46.0", "Plan pages")];
        using (var first = CreateStore())
            await first.GetAsync(CancellationToken.None);

        using var restarted = CreateStore();
        var notes = await restarted.GetAsync(CancellationToken.None);

        notes.Releases.Single().Version.ShouldBe("0.46.0");
        _github.Requests.ShouldBe(1);
    }

    [Fact]
    public async Task Notes_older_than_the_update_check_interval_are_fetched_again()
    {
        _github.Releases = [Release("v0.45.0", "Pinned sessions")];
        using var store = CreateStore();
        await store.GetAsync(CancellationToken.None);

        _time.Advance(TimeSpan.FromHours(3));
        await store.GetAsync(CancellationToken.None);
        _github.Requests.ShouldBe(1);

        _github.Releases = [Release("v0.46.0", "Plan pages"), Release("v0.45.0", "Pinned sessions")];
        _time.Advance(TimeSpan.FromHours(2));
        var notes = await store.GetAsync(CancellationToken.None);

        _github.Requests.ShouldBe(2);
        notes.Releases.Select(r => r.Version).ShouldBe(["0.46.0", "0.45.0"]);
    }

    [Fact]
    public async Task When_GitHub_fails_the_saved_notes_come_back_with_the_error()
    {
        _github.Releases = [Release("v0.45.0", "Pinned sessions")];
        using var store = CreateStore();
        await store.GetAsync(CancellationToken.None);

        _github.Status = HttpStatusCode.Forbidden;
        _time.Advance(TimeSpan.FromHours(5));
        var notes = await store.GetAsync(CancellationToken.None);

        notes.Releases.Single().Version.ShouldBe("0.45.0");
        notes.Error.ShouldBe("GitHub returned 403");
    }

    [Fact]
    public async Task It_keeps_at_most_twenty_releases()
    {
        _github.Releases = Enumerable.Range(1, 25).Select(i => Release($"v0.{i}.0", $"Release {i}")).ToList();
        using var store = CreateStore();

        var notes = await store.GetAsync(CancellationToken.None);

        notes.Releases.Count.ShouldBe(ReleaseNotesStore.ReleaseCount);
        notes.Releases[0].Version.ShouldBe("0.25.0");
        notes.Releases[^1].Version.ShouldBe("0.6.0");
    }

    [Fact]
    public async Task The_update_check_takes_the_newest_published_release_and_saves_the_notes()
    {
        _github.Releases =
        [
            Release("v0.99.0", "Next", prerelease: true),
            Release("v0.46.0", "Plan pages"),
            Release("v0.45.0", "Pinned sessions"),
        ];
        var options = Options();
        var factory = new TestHttpClientFactory(_github);
        var stateHolder = new UpdateStateHolder();
        // The first state the check reports names the version it found (a download may follow; this fake can't serve one).
        string? found = null;
        stateHolder.StateChanged += state => found ??= state.LatestVersion;
        var download = new UpdateDownloadService(factory, stateHolder, NullLogger<UpdateDownloadService>.Instance);
        using var store = new ReleaseNotesStore(factory, options, NullLogger<ReleaseNotesStore>.Instance, _time);
        using var check = new UpdateCheckService(factory, options, stateHolder, download, store, NullLogger<UpdateCheckService>.Instance);

        await check.CheckForUpdateAsync(CancellationToken.None);
        var requestsByCheck = _github.Requests;
        var notes = await store.GetAsync(CancellationToken.None);

        found.ShouldBe("0.46.0");
        notes.Releases.Select(r => r.Version).ShouldBe(["0.46.0", "0.45.0"]);
        _github.Requests.ShouldBe(requestsByCheck);
    }

    private ReleaseNotesStore CreateStore() =>
        new(new TestHttpClientFactory(_github), Options(), NullLogger<ReleaseNotesStore>.Instance, _time);

    private FleetOptions Options() => new()
    {
        DatabasePath = Path.Combine(_dataDir, "fleet.db"),
        Update = new UpdateOptions { GitHubRepo = "acme/fleet-releases", CheckIntervalHours = 4 },
    };

    private static JsonObject Release(string tag, string body, bool draft = false, bool prerelease = false) => new()
    {
        ["tag_name"] = tag,
        ["html_url"] = $"https://github.com/acme/fleet-releases/releases/tag/{tag}",
        ["body"] = body,
        ["published_at"] = "2026-10-07T08:06:17Z",
        ["draft"] = draft,
        ["prerelease"] = prerelease,
    };

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>Answers GitHub's list-releases call with <see cref="Releases"/>, and counts the calls.</summary>
    private sealed class FakeGitHub : HttpMessageHandler
    {
        public List<JsonObject> Releases { get; set; } = [];
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public int Requests { get; private set; }
        public string? LastPathAndQuery { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            LastPathAndQuery = request.RequestUri!.PathAndQuery;
            if (Status != HttpStatusCode.OK)
                return Task.FromResult(new HttpResponseMessage(Status));

            var body = new JsonArray(Releases.Select(r => (JsonNode)r.DeepClone()).ToArray());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}

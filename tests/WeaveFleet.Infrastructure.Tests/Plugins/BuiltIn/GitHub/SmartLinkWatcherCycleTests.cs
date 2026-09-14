using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Plugins.BuiltIn.GitHub;
using WeaveFleet.Infrastructure.Services;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;
using WeaveFleet.Testing.Fixtures;

namespace WeaveFleet.Infrastructure.Tests.Plugins.BuiltIn.GitHub;

/// <summary>How many requests a watcher cycle sends to GitHub.</summary>
public sealed class SmartLinkWatcherCycleTests : IDisposable
{
    private const string UserId = "test-user";
    private const string BranchX = "/repos/acme/rocket/pulls?head=acme%3Afeature%2Fx&state=all&per_page=5";
    private const string BranchY = "/repos/acme/rocket/pulls?head=acme%3Afeature%2Fy&state=all&per_page=5";
    private const string PullRequest7 = "/repos/acme/rocket/pulls/7";
    private const string CheckRuns = "/repos/acme/rocket/commits/abc123/check-runs";

    private readonly RealGitRepository _git = new();
    private readonly StubClock _clock = new(DateTimeOffset.Parse("2026-09-14T09:00:00Z", CultureInfo.InvariantCulture));
    private readonly FakeGitHub _gitHub = new();
    private readonly InMemorySmartLinkRepository _links = new();
    private readonly SmartLinkDetector _detector;
    private readonly SmartLinkWatcherService _watcher;

    public SmartLinkWatcherCycleTests()
    {
        _git.Git("remote", "add", "origin", "https://github.com/acme/rocket.git");
        _detector = new SmartLinkDetector(_clock);

        var httpClientFactory = new TestHttpClientFactory(_gitHub);
        var credentials = new InMemoryUserCredentialRepository();
        credentials.Seed(new UserCredential
        {
            Id = "cred-1",
            UserId = UserId,
            Namespace = "github",
            Kind = "oauth-access-token",
            Label = "GitHub",
            EncryptedValue = "ENC:token",
            DisplayHint = "...oken",
            CreatedAt = "2026-01-01T00:00:00Z",
            UpdatedAt = "2026-01-01T00:00:00Z",
        });

        var services = new ServiceCollection();
        services.AddSingleton<ISmartLinkRepository>(_links);
        services.AddSingleton(new GitHubService(httpClientFactory, new FakePluginStateStore(), credentials, new FakeCredentialProtector()));
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        _watcher = new SmartLinkWatcherService(
            scopeFactory,
            new GitHubApiProxy(httpClientFactory),
            _detector,
            new FakeEventBroadcaster(),
            new RepositoryService(scopeFactory, NullLogger<RepositoryService>.Instance),
            NullLogger<SmartLinkWatcherService>.Instance,
            _clock);
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _git.Dispose();
    }

    [Fact]
    public async Task Sessions_on_one_branch_and_links_to_one_pull_request_share_each_request()
    {
        OnBranch("s1", "feature/x");
        OnBranch("s2", "feature/x");
        OnBranch("s3", "feature/y");
        _links.Seed(Link("s4", 7));

        await _watcher.RunCycleAsync(CancellationToken.None);

        _gitHub.Count(BranchX).ShouldBe(1);
        _gitHub.Count(BranchY).ShouldBe(1);
        _gitHub.Count(PullRequest7).ShouldBe(1);
        _gitHub.Count(CheckRuns).ShouldBe(1);
        _gitHub.Count("/graphql").ShouldBe(1);
        _links.All.Where(l => l.ResourceId == "acme/rocket#7").Select(l => (l.SessionId, l.EnrichmentStatus))
            .ShouldBe([("s4", "resolved"), ("s1", "resolved"), ("s2", "resolved")], ignoreOrder: true);
    }

    [Fact]
    public async Task After_startup_branches_are_looked_up_only_for_busy_sessions()
    {
        OnBranch("s1", "feature/x");
        OnBranch("s3", "feature/y");

        await _watcher.RunCycleAsync(CancellationToken.None);
        _clock.Now = _clock.Now.AddMinutes(3);
        _detector.Observe("s1", UserId, "session.status", null);
        await _watcher.RunCycleAsync(CancellationToken.None);

        _gitHub.Count(BranchX).ShouldBe(2);
        _gitHub.Count(BranchY).ShouldBe(1);
    }

    [Fact]
    public async Task A_link_on_a_quiet_session_waits_for_the_longer_interval()
    {
        var link = Link("s1", 7);
        link.EnrichmentStatus = SmartLinkEnrichmentStatuses.Resolved;
        link.LastCheckedAt = _clock.Now.AddMinutes(-1).UtcDateTime.ToString("O");
        _links.Seed(link);

        await _watcher.RunCycleAsync(CancellationToken.None);
        _gitHub.Count(PullRequest7).ShouldBe(0);

        _detector.Observe("s1", UserId, "session.status", null);
        await _watcher.RunCycleAsync(CancellationToken.None);
        _gitHub.Count(PullRequest7).ShouldBe(1);
    }

    [Fact]
    public async Task A_refused_token_backs_off_instead_of_retrying_every_cycle()
    {
        _gitHub.Unauthorized = true;
        _links.Seed(Link("s1", 7));

        await _watcher.RunCycleAsync(CancellationToken.None);
        _clock.Now = _clock.Now.AddSeconds(10);
        await _watcher.RunCycleAsync(CancellationToken.None);

        _gitHub.Count(PullRequest7).ShouldBe(1);
        _links.All.Single().EnrichmentStatus.ShouldBe(SmartLinkEnrichmentStatuses.NotConnected);
    }

    private void OnBranch(string sessionId, string branch)
        => _links.BranchTargets.Add(new SmartLinkBranchTarget(sessionId, UserId, branch, _git.Path, null));

    private static SmartLink Link(string sessionId, int number) => new()
    {
        Id = Guid.NewGuid().ToString(),
        SessionId = sessionId,
        Url = $"https://github.com/acme/rocket/pull/{number}",
        ProviderId = "github",
        ResourceType = GitHubLinkReference.PullRequest,
        ResourceId = $"acme/rocket#{number}",
        Title = $"acme/rocket#{number}",
        UserId = UserId,
        Relationship = SmartLinkRelationships.Pinned,
    };

    private sealed class StubClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>
    /// Answers as GitHub would for one open pull request, #7 from feature/x, and counts requests by path
    /// and query.
    /// </summary>
    private sealed class FakeGitHub : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);

        public bool Unauthorized { get; set; }

        public int Count(string pathAndQuery) => _counts.GetValueOrDefault(pathAndQuery);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var pathAndQuery = request.RequestUri!.PathAndQuery;
            _counts.AddOrUpdate(pathAndQuery, 1, (_, count) => count + 1);

            if (Unauthorized)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));

            JsonNode body = pathAndQuery switch
            {
                BranchX => new JsonArray { new JsonObject { ["number"] = 7 } },
                PullRequest7 => new JsonObject
                {
                    ["number"] = 7,
                    ["title"] = "Ship it",
                    ["state"] = "open",
                    ["merged"] = false,
                    ["draft"] = false,
                    ["html_url"] = "https://github.com/acme/rocket/pull/7",
                    ["head"] = new JsonObject { ["sha"] = "abc123", ["ref"] = "feature/x" },
                    ["base"] = new JsonObject { ["ref"] = "main" },
                },
                CheckRuns => new JsonObject { ["check_runs"] = new JsonArray() },
                "/graphql" => new JsonObject
                {
                    ["data"] = new JsonObject
                    {
                        ["repository"] = new JsonObject
                        {
                            ["pullRequest"] = new JsonObject { ["reviewThreads"] = new JsonObject { ["nodes"] = new JsonArray() } },
                        },
                    },
                },
                _ => new JsonArray(),
            };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            });
        }
    }
}

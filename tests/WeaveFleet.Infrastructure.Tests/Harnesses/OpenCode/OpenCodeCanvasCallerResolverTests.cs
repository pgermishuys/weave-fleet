using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Canvases;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;
using WeaveFleet.Infrastructure.Harnesses.OpenCode.Pooling;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode;

#pragma warning disable CA1001 // Type owns disposable fields — disposal handled by IAsyncLifetime.DisposeAsync
public sealed class OpenCodeCanvasCallerResolverTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private const string RepoA = "/repo/a";
    private const string RepoB = "/repo/b";

    private readonly FakeOpenCodeSessions _sessions = new();
    private readonly PoolDemuxBindingTable _bindings = new();
    private PooledOpenCodeInstanceRegistry _registry = null!;
    private OpenCodeCanvasCallerResolver _resolver = null!;
    private InstanceLease _leaseA = null!;
    private InstanceLease _leaseB = null!;

    private PooledOpenCodeInstance InstanceA => _leaseA.Instance;
    private PooledOpenCodeInstance InstanceB => _leaseB.Instance;

    public async Task InitializeAsync()
    {
        var spawned = 0;
        _registry = new PooledOpenCodeInstanceRegistry(
            (key, _, _, _) =>
            {
                var number = Interlocked.Increment(ref spawned);
                var httpClient = new OpenCodeHttpClient(
                    new HttpClient(_sessions) { BaseAddress = new Uri("http://127.0.0.1:4096") },
                    NullLogger<OpenCodeHttpClient>.Instance);
                return Task.FromResult(new PooledOpenCodeInstance(key, $"instance-{number}", null, httpClient, null, () => ValueTask.CompletedTask)
                {
                    BridgeToken = $"token-{number}",
                });
            },
            TimeSpan.FromMinutes(1),
            NullLogger<PooledOpenCodeInstanceRegistry>.Instance);
        _resolver = new OpenCodeCanvasCallerResolver(_registry, _bindings, NullLogger<OpenCodeCanvasCallerResolver>.Instance);

        _leaseA = await _registry.AcquireAsync("credential-a", RepoA, CancellationToken.None);
        _leaseB = await _registry.AcquireAsync("credential-b", RepoA, CancellationToken.None);
        InstanceA.BridgeToken.ShouldBe("token-1");
        InstanceB.BridgeToken.ShouldBe("token-2");
    }

    public async Task DisposeAsync()
    {
        await _leaseA.DisposeAsync();
        await _leaseB.DisposeAsync();
        await _registry.DisposeAsync();
        _sessions.Dispose();
    }

    [Fact]
    public async Task resolves_a_session_bound_to_the_process_that_owns_the_token()
    {
        Bind(InstanceA, "oc-1", "fleet-1", "user-1");

        var caller = await _resolver.ResolveAsync("token-1", "oc-1");

        caller.ShouldBe(new HarnessCanvasCaller("fleet-1", "user-1"));
    }

    [Fact]
    public async Task a_token_from_another_process_does_not_resolve_the_session()
    {
        Bind(InstanceB, "oc-1", "fleet-1", "user-1");

        (await _resolver.ResolveAsync("token-1", "oc-1")).ShouldBeNull();
        (await _resolver.ResolveAsync("token-2", "oc-1")).ShouldNotBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("token-9")]
    [InlineData("token-1 ")]
    public async Task an_unknown_or_missing_token_does_not_resolve(string token)
    {
        Bind(InstanceA, "oc-1", "fleet-1", "user-1");

        (await _resolver.ResolveAsync(token, "oc-1")).ShouldBeNull();
    }

    [Fact]
    public async Task an_unknown_session_does_not_resolve()
    {
        Bind(InstanceA, "oc-1", "fleet-1", "user-1");

        (await _resolver.ResolveAsync("token-1", "oc-unknown")).ShouldBeNull();
    }

    [Fact]
    public async Task a_binding_moved_to_a_new_process_resolves_through_that_process_token_only()
    {
        var consumerId = Bind(InstanceA, "oc-1", "fleet-1", "user-1");

        _bindings.MoveBindings(InstanceA, InstanceB, consumerId, RepoA, sourceLeaseGeneration: 1, targetLeaseGeneration: 2);

        (await _resolver.ResolveAsync("token-2", "oc-1")).ShouldBe(new HarnessCanvasCaller("fleet-1", "user-1"));
        (await _resolver.ResolveAsync("token-1", "oc-1")).ShouldBeNull();
    }

    [Fact]
    public async Task a_stopped_process_token_no_longer_resolves()
    {
        Bind(InstanceA, "oc-1", "fleet-1", "user-1");

        await InstanceA.DisposeAsync();

        (await _resolver.ResolveAsync("token-1", "oc-1")).ShouldBeNull();
    }

    [Fact]
    public async Task a_subagent_session_resolves_to_the_bound_session_that_started_it()
    {
        Bind(InstanceA, "oc-1", "fleet-1", "user-1");
        _sessions.Add(RepoA, "oc-child", parentId: "oc-1");

        (await _resolver.ResolveAsync("token-1", "oc-child")).ShouldBe(new HarnessCanvasCaller("fleet-1", "user-1"));
    }

    [Fact]
    public async Task the_parent_walk_follows_up_to_three_parents()
    {
        Bind(InstanceA, "oc-1", "fleet-1", "user-1");
        _sessions.Add(RepoA, "oc-s1", parentId: "oc-1");
        _sessions.Add(RepoA, "oc-s2", parentId: "oc-s1");
        _sessions.Add(RepoA, "oc-s3", parentId: "oc-s2");
        _sessions.Add(RepoA, "oc-s4", parentId: "oc-s3");

        (await _resolver.ResolveAsync("token-1", "oc-s3")).ShouldNotBeNull();
        (await _resolver.ResolveAsync("token-1", "oc-s4")).ShouldBeNull();
    }

    [Fact]
    public async Task a_top_level_session_that_is_not_bound_does_not_resolve()
    {
        Bind(InstanceA, "oc-1", "fleet-1", "user-1");
        _sessions.Add(RepoA, "oc-other", parentId: null);

        (await _resolver.ResolveAsync("token-1", "oc-other")).ShouldBeNull();
    }

    [Fact]
    public async Task the_parent_walk_only_reaches_sessions_bound_to_the_calling_process()
    {
        Bind(InstanceB, "oc-1", "fleet-1", "user-1");
        Bind(InstanceA, "oc-2", "fleet-2", "user-1");
        _sessions.Add(RepoA, "oc-child", parentId: "oc-1");

        (await _resolver.ResolveAsync("token-1", "oc-child")).ShouldBeNull();
    }

    [Fact]
    public async Task the_parent_is_looked_up_in_each_workspace_bound_to_the_process()
    {
        Bind(InstanceA, "oc-1", "fleet-1", "user-1", RepoA);
        Bind(InstanceA, "oc-2", "fleet-2", "user-1", RepoB);
        _sessions.Add(RepoB, "oc-child", parentId: "oc-2");

        (await _resolver.ResolveAsync("token-1", "oc-child")).ShouldBe(new HarnessCanvasCaller("fleet-2", "user-1"));
    }

    [Fact]
    public async Task a_failing_parent_lookup_does_not_resolve()
    {
        Bind(InstanceA, "oc-1", "fleet-1", "user-1");
        _sessions.FailWith = HttpStatusCode.InternalServerError;

        (await _resolver.ResolveAsync("token-1", "oc-child")).ShouldBeNull();
    }

    private Guid Bind(PooledOpenCodeInstance instance, string openCodeSessionId, string fleetSessionId, string userId, string directory = RepoA)
    {
        var consumerId = Guid.NewGuid();
        _bindings.Bind(instance, openCodeSessionId, consumerId, fleetSessionId, userId, directory, leaseGeneration: 1);
        return consumerId;
    }

    /// <summary>Answers GET /session/{id}?directory=… like OpenCode: 404 unless the session lives in that workspace.</summary>
    private sealed class FakeOpenCodeSessions : HttpMessageHandler
    {
        private readonly Dictionary<(string Directory, string Id), string?> _sessions = [];

        public HttpStatusCode? FailWith { get; set; }

        public void Add(string directory, string id, string? parentId) => _sessions[(directory, id)] = parentId;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (FailWith is { } status)
                return Task.FromResult(new HttpResponseMessage(status));

            var path = request.RequestUri!.AbsolutePath;
            if (request.Method != HttpMethod.Get || !path.StartsWith("/session/", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            var id = Uri.UnescapeDataString(path["/session/".Length..]);
            var directory = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["directory"] ?? string.Empty;
            if (!_sessions.TryGetValue((directory, id), out var parentId))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            var json = parentId is null
                ? $$"""{"id":"{{id}}","title":"t"}"""
                : $$"""{"id":"{{id}}","parentID":"{{parentId}}","title":"t"}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}

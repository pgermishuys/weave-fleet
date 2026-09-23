using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode;

public sealed class OpenCodeCatalogCacheTests
{
    private const string Folder = "/work/alpha";
    private readonly Clock _clock = new(new DateTimeOffset(2026, 9, 23, 7, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Callers_asking_at_once_share_one_fetch_and_later_callers_get_the_kept_answer()
    {
        var cache = new OpenCodeCatalogCache(_clock);
        var fetches = 0;
        var answer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = cache.GetAsync("provider", Folder, _ => { Interlocked.Increment(ref fetches); return answer.Task; }, CancellationToken.None);
        var second = cache.GetAsync("provider", Folder, _ => { Interlocked.Increment(ref fetches); return answer.Task; }, CancellationToken.None);
        answer.SetResult("providers");

        (await first).ShouldBe("providers");
        (await second).ShouldBe("providers");
        (await cache.GetAsync("provider", Folder, _ => Task.FromResult("again"), CancellationToken.None)).ShouldBe("providers");
        fetches.ShouldBe(1);
    }

    [Fact]
    public async Task A_stale_answer_is_handed_back_at_once_while_a_new_one_is_fetched_behind_it()
    {
        var cache = new OpenCodeCatalogCache(_clock);
        await cache.GetAsync("agent", Folder, _ => Task.FromResult("old"), CancellationToken.None);
        _clock.Advance(OpenCodeCatalogCache.FreshFor);

        var refresh = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stale = cache.GetAsync("agent", Folder, _ => refresh.Task, CancellationToken.None);
        stale.IsCompletedSuccessfully.ShouldBeTrue("a stale answer must not wait for the refresh");
        (await stale).ShouldBe("old");

        refresh.SetResult("new");
        await WaitUntil(async () => await cache.GetAsync("agent", Folder, _ => Task.FromResult("unused"), CancellationToken.None) == "new");
    }

    [Fact]
    public async Task One_caller_giving_up_does_not_cancel_the_fetch_the_others_wait_on()
    {
        var cache = new OpenCodeCatalogCache(_clock);
        var answer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fetchToken = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var leaving = new CancellationTokenSource();

        var gone = cache.GetAsync("command", Folder, ct => { fetchToken.SetResult(ct); return answer.Task; }, leaving.Token);
        var staying = cache.GetAsync("command", Folder, _ => answer.Task, CancellationToken.None);
        await leaving.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => gone);
        (await fetchToken.Task).CanBeCanceled.ShouldBeFalse();
        answer.SetResult("commands");
        (await staying).ShouldBe("commands");
    }

    [Fact]
    public async Task A_failed_fetch_is_not_kept()
    {
        var cache = new OpenCodeCatalogCache(_clock);

        await Should.ThrowAsync<HttpRequestException>(() =>
            cache.GetAsync<string>("provider", Folder, _ => throw new HttpRequestException("OpenCode is restarting"), CancellationToken.None));

        (await cache.GetAsync("provider", Folder, _ => Task.FromResult("providers"), CancellationToken.None)).ShouldBe("providers");
    }

    [Fact]
    public async Task Forgetting_a_folder_drops_only_its_answers()
    {
        var cache = new OpenCodeCatalogCache(_clock);
        await cache.GetAsync("agent", Folder, _ => Task.FromResult("alpha agents"), CancellationToken.None);
        await cache.GetAsync("agent", "/work/beta", _ => Task.FromResult("beta agents"), CancellationToken.None);

        cache.Forget(Folder);

        (await cache.GetAsync("agent", Folder, _ => Task.FromResult("alpha agents, reloaded"), CancellationToken.None)).ShouldBe("alpha agents, reloaded");
        (await cache.GetAsync("agent", "/work/beta", _ => Task.FromResult("unused"), CancellationToken.None)).ShouldBe("beta agents");
    }

    [Fact]
    public async Task The_client_asks_OpenCode_once_per_folder_until_the_folder_is_reloaded()
    {
        var openCode = new CountingOpenCode();
        var root = new OpenCodeHttpClient(
            new HttpClient(openCode) { BaseAddress = new Uri("http://127.0.0.1:4096") },
            NullLogger<OpenCodeHttpClient>.Instance,
            _clock);
        // A session reads through its own folder-scoped copy of the process's client.
        var session = root.WithExpectedDirectory(Folder);

        await session.GetProvidersAsync(Folder, CancellationToken.None);
        await session.GetAgentsAsync(Folder, CancellationToken.None);
        await root.GetProvidersAsync(Folder, CancellationToken.None);
        await session.GetAgentsAsync(Folder, CancellationToken.None);
        openCode.Count("/provider").ShouldBe(1);
        openCode.Count("/agent").ShouldBe(1);

        await root.DisposeInstanceAsync(Folder, CancellationToken.None);
        await session.GetProvidersAsync(Folder, CancellationToken.None);
        openCode.Count("/provider").ShouldBe(2);
    }

    private static async Task WaitUntil(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 100; i++)
        {
            if (await condition())
                return;
            await Task.Delay(10);
        }
        throw new TimeoutException("condition never held");
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class CountingOpenCode : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);

        public int Count(string path) => _counts.GetValueOrDefault(path);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            _counts.AddOrUpdate(path, 1, (_, n) => n + 1);
            var body = path switch
            {
                "/provider" => """{ "all": [], "connected": [], "default": null }""",
                "/agent" => "[]",
                "/instance/dispose" => "true",
                _ => throw new InvalidOperationException($"Unexpected request {request.Method} {path}"),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}

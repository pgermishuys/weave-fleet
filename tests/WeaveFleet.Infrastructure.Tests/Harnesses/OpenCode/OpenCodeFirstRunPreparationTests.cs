using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;
using WeaveFleet.Infrastructure.Harnesses.OpenCode.Pooling;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode;

public sealed class OpenCodeFirstRunPreparationTests
{
    private const string Directory = "/work/project";

    [Fact]
    public async Task It_loads_the_folder_by_asking_for_its_config()
    {
        var handler = new FakeOpenCode(_ => Task.FromResult(Json("{}")));

        await OpenCodeFirstRunPreparation.RunAsync(Client(handler), Directory, NullLogger.Instance, TimeSpan.FromSeconds(30));

        handler.Requests.ShouldBe([$"GET /config?directory={Uri.EscapeDataString(Directory)}"]);
    }

    [Fact]
    public async Task It_waits_as_long_as_OpenCode_takes_to_install_its_packages()
    {
        var release = new TaskCompletionSource<HttpResponseMessage>();
        var handler = new FakeOpenCode(_ => release.Task);

        var preparing = OpenCodeFirstRunPreparation.RunAsync(Client(handler), Directory, NullLogger.Instance, TimeSpan.FromSeconds(30));
        await Task.Delay(100);
        preparing.IsCompleted.ShouldBeFalse();

        release.SetResult(Json("{}"));
        await preparing.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task It_does_not_throw_when_OpenCode_answers_with_an_error()
    {
        var handler = new FakeOpenCode(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        await OpenCodeFirstRunPreparation.RunAsync(Client(handler), Directory, NullLogger.Instance, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task It_gives_up_after_its_timeout_without_throwing()
    {
        var handler = new FakeOpenCode(async cancel =>
        {
            await Task.Delay(Timeout.Infinite, cancel);
            return Json("{}");
        });

        await OpenCodeFirstRunPreparation.RunAsync(Client(handler), Directory, NullLogger.Instance, TimeSpan.FromMilliseconds(100))
            .WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_caller_waits_for_the_preparation_and_leaves_early_when_cancelled()
    {
        var preparation = new TaskCompletionSource();
        await using var instance = new PooledOpenCodeInstance("key", "instance", null, () => ValueTask.CompletedTask)
        {
            Preparation = preparation.Task,
        };

        var waiting = instance.WhenPreparedAsync(CancellationToken.None);
        await Task.Delay(50);
        waiting.IsCompleted.ShouldBeFalse();

        using var cts = new CancellationTokenSource();
        var cancellable = instance.WhenPreparedAsync(cts.Token);
        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(cancellable);
        preparation.Task.IsCompleted.ShouldBeFalse();

        preparation.SetResult();
        await waiting.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task The_registry_hands_out_a_lease_only_after_the_process_is_prepared()
    {
        var preparation = new TaskCompletionSource();
        await using var registry = Registry(preparation.Task);
        var environment = Environment();

        var acquiring = registry.AcquireAsync("local-user", CredentialHasher.HashEnvironment(environment), environment, Directory, CancellationToken.None);
        await Task.Delay(100);
        acquiring.IsCompleted.ShouldBeFalse();

        preparation.SetResult();
        await using var lease = await acquiring.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_warmup_acquire_does_not_wait_for_the_preparation()
    {
        var preparation = new TaskCompletionSource();
        await using var registry = Registry(preparation.Task);
        var environment = Environment();

        await using var lease = await registry
            .AcquireAsync("local-user", CredentialHasher.HashEnvironment(environment), environment, Directory, CancellationToken.None, waitUntilPrepared: false)
            .WaitAsync(TimeSpan.FromSeconds(10));

        preparation.Task.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task A_cancelled_acquire_gives_its_lease_back()
    {
        var preparation = new TaskCompletionSource();
        await using var registry = Registry(preparation.Task);
        var environment = Environment();
        using var cts = new CancellationTokenSource();

        var acquiring = registry.AcquireAsync("local-user", CredentialHasher.HashEnvironment(environment), environment, Directory, cts.Token);
        await Task.Delay(100);
        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(acquiring);

        registry.GetHealthStatus().ActiveCount.ShouldBe(0);
    }

    private static PooledOpenCodeInstanceRegistry Registry(Task preparation) => new(
        (key, _, _, _) => Task.FromResult(new PooledOpenCodeInstance(key, "instance", null, () => ValueTask.CompletedTask)
        {
            Preparation = preparation,
        }),
        TimeSpan.FromMinutes(1),
        NullLogger<PooledOpenCodeInstanceRegistry>.Instance);

    private static Dictionary<string, string> Environment() => new(StringComparer.Ordinal) { ["EXAMPLE"] = "value" };

    private static OpenCodeHttpClient Client(HttpMessageHandler handler) => new(
        new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:4096") },
        NullLogger<OpenCodeHttpClient>.Instance);

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };

    private sealed class FakeOpenCode(Func<CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add($"{request.Method} {request.RequestUri!.PathAndQuery}");
            return respond(cancellationToken);
        }
    }
}

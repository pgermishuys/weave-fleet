using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Runtimes;
using WeaveFleet.Domain.Common;

namespace WeaveFleet.Api.Tests.Endpoints;

/// <summary>The mod runtime's API against a fake Bun installer; nothing here reaches the network.</summary>
public sealed class ModsRuntimeEndpointTests : IAsyncDisposable
{
    private const string Runtime = "/api/features/mods/runtime";

    private readonly ScriptedBunRuntime _bun = new();
    private readonly ApiWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ModsRuntimeEndpointTests()
    {
        _factory = new ApiWebApplicationFactory(authEnabled: false, configureTestServices: services => services.AddSingleton<IBunRuntime>(_bun));
        _client = _factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        _bun.Gate.TrySetResult();
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private async Task<bool> SwitchOnAsync() => (await _client.GetFromJsonAsync<JsonElement>("/api/features/mods")).GetProperty("on").GetBoolean();

    [Fact]
    public async Task The_view_is_there_while_the_switch_is_off()
    {
        var view = await _client.GetFromJsonAsync<JsonElement>(Runtime);

        view.GetProperty("bun").ValueKind.ShouldBe(JsonValueKind.Null);
        view.GetProperty("job").ValueKind.ShouldBe(JsonValueKind.Null);
        var release = view.GetProperty("release");
        release.GetProperty("version").GetString().ShouldBe("1.4.2");
        release.GetProperty("hasBuild").GetBoolean().ShouldBeTrue();
        release.GetProperty("source").GetString().ShouldBe("github.com/oven-sh/bun");
        (await SwitchOnAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task Installing_turns_the_switch_on_and_answers_202_while_it_runs()
    {
        _bun.Hold = true;

        var response = await _client.PostAsync($"{Runtime}/install", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await SwitchOnAsync()).ShouldBeTrue();
        await _bun.Started.Task;
        (await _client.PostAsync($"{Runtime}/install", null)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        _bun.Ensures.ShouldBe(1);
    }

    [Fact]
    public async Task Installing_when_the_releases_bun_is_there_answers_200_and_downloads_nothing()
    {
        _bun.Installed = new BunLocation("/bun/1.4.2/bun", BunSources.Installed, "1.4.2");

        var response = await _client.PostAsync($"{Runtime}/install", null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("bun").GetProperty("version").GetString().ShouldBe("1.4.2");
        _bun.Ensures.ShouldBe(0);
    }

    [Fact]
    public async Task Installing_is_refused_with_409_when_the_configuration_sets_a_bun()
    {
        _factory.Services.GetRequiredService<FleetOptions>().Harness.BunPath = "/opt/tools/bun/bin/bun";

        var response = await _client.PostAsync($"{Runtime}/install", null);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        _bun.Ensures.ShouldBe(0);
    }

    [Fact]
    public async Task Cancelling_with_nothing_running_is_409()
    {
        var response = await _client.PostAsync($"{Runtime}/cancel", null);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString().ShouldBe("No install is running.");
    }

    [Fact]
    public async Task Cancelling_ends_the_install_and_turns_the_switch_back_off()
    {
        _bun.Hold = true;
        (await _client.PostAsync($"{Runtime}/install", null)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await _bun.Started.Task;
        (await SwitchOnAsync()).ShouldBeTrue();

        var response = await _client.PostAsync($"{Runtime}/cancel", null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var job = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("job");
        job.GetProperty("phase").GetString().ShouldBe("failed");
        job.GetProperty("reason").GetString().ShouldBe("cancelled");
        (await SwitchOnAsync()).ShouldBeFalse();
    }

    /// <summary>A Bun installer that installs on cue: with <see cref="Hold"/> it waits for a cancel.</summary>
    private sealed class ScriptedBunRuntime : IBunRuntime
    {
        public int Ensures { get; private set; }
        public bool Hold { get; set; }
        public BunLocation? Installed { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public BunInstallJob? Job => null;

        public Task<BunLocation?> FindAsync(BunRelease release, CancellationToken ct) => Task.FromResult(Installed);

        public async Task<Result<BunLocation>> EnsureAsync(BunRelease release, IProgress<BunInstallJob>? progress, CancellationToken ct)
        {
            Ensures++;
            Started.TrySetResult();
            try
            {
                if (Hold)
                    await Gate.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                progress?.Report(new BunInstallJob(BunInstallPhases.Failed, release.Version, "The install was cancelled.", 0, null, BunInstallFailures.Cancelled));
                throw;
            }

            return Installed = new BunLocation($"/bun/{release.Version}/bun", BunSources.Installed, release.Version);
        }

        public Task<IReadOnlyList<BunCandidate>> FindOnMachineAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<BunCandidate> CheckAsync(string path, CancellationToken ct) => throw new NotSupportedException();
        IReadOnlyList<BunLocation> IBunRuntime.Installed() => Installed is null ? [] : [Installed];
        public Task<IReadOnlyList<string>> PruneAsync(IReadOnlyCollection<string> inUse, CancellationToken ct) => throw new NotSupportedException();
        public BunSafety SafetyOf(BunLocation location, BunRelease release) => new(true, false, null);
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using WeaveFleet.Api.Tests.Auth;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Devices;
using WeaveFleet.Application.Machines;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Application.Updates;
using WeaveFleet.Application.Users;
using WeaveFleet.Infrastructure.Updates;

namespace WeaveFleet.Api.Tests.Endpoints;

/// <summary>
/// Restarting Fleet into an update and rolling an update out: only the owner, never an agent or a paired phone, and only
/// when something is staged and nothing is in a turn.
/// </summary>
[Collection(LocalTokenAuthServiceTestsGroup.Name)]
public sealed class UpdateApplyEndpointTests : IDisposable
{
    private const string BridgeToken = "bridge-1";

    private readonly string _root = Directory.CreateTempSubdirectory("fleet-apply-").FullName;
    private readonly FakeTimeProvider _time = new();
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly UpdateRestartGate _gate = new();
    private string? _staged = "99.0.0";
    private int _working;
    private bool _launcher = true;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private ApiWebApplicationFactory Fleet() => new(
        authEnabled: false,
        tokenAuthEnabled: true,
        simulateLocalhostRequest: true,
        host: "0.0.0.0",
        configureTestServices: services =>
        {
            services.AddSingleton<IHarnessBridgeTokens>(new FakeBridgeTokens());
            services.AddSingleton(_gate);
            services.AddSingleton(sp => new UpdateRestartService(
                sp.GetRequiredService<FleetOptions>(),
                new UpdateFiles(_root),
                sp.GetRequiredService<UpdateDownloadService>(),
                _gate,
                sp.GetRequiredService<MachineCapabilitiesReader>(),
                new StubLifetime(),
                _time,
                NullLogger<UpdateRestartService>.Instance,
                code => _exited.TrySetResult(code),
                name => _launcher && name == UpdateRestartService.LauncherVariable ? "85" : null,
                () => _staged,
                () => Task.FromResult(_working)));
        });

    private static HttpClient Client(ApiWebApplicationFactory factory, string token)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static string OwnerToken(ApiWebApplicationFactory factory) => factory.Services.GetRequiredService<ILocalTokenAuthService>().Token;

    [Fact]
    public async Task The_owner_restarts_into_the_staged_update_and_new_turns_are_refused_meanwhile()
    {
        await using var factory = Fleet();
        using var owner = Client(factory, OwnerToken(factory));

        var response = await owner.PostAsync("/api/update/apply", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("to").GetString().ShouldBe("99.0.0");
        _exited.Task.IsCompleted.ShouldBeFalse("the response goes out first");
        _time.Advance(TimeSpan.FromSeconds(1));
        (await _exited.Task).ShouldBe(UpdateRestartService.ExitCode);

        var prompt = await owner.PostAsJsonAsync("/api/sessions/s1/prompt", new { text = "hello" });
        prompt.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await prompt.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!.ShouldContain("restarting");
    }

    [Fact]
    public async Task Nothing_staged_a_running_turn_or_no_way_back_up_each_answer_conflict_and_say_why()
    {
        await using var factory = Fleet();
        using var owner = Client(factory, OwnerToken(factory));

        _staged = null;
        await ConflictAsync(owner, "No update is downloaded yet.");

        _staged = "99.0.0";
        _working = 2;
        await ConflictAsync(owner, "2 sessions are working.");
        _gate.IsRestarting.ShouldBeFalse("a refused restart leaves the gate open");

        _working = 0;
        _launcher = false;
        await ConflictAsync(owner, "Restart Fleet once by hand; after that it can restart itself.");
        _exited.Task.IsCompleted.ShouldBeFalse();
    }

    [Theory]
    [InlineData("/api/update/apply")]
    [InlineData("/api/update/rollout")]
    public async Task An_agent_cannot_restart_fleet_or_start_a_rollout(string path)
    {
        await using var factory = Fleet();
        using var client = factory.CreateClient();

        var response = await client.PostAsync($"/agent/{BridgeToken}{path}", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        _exited.Task.IsCompleted.ShouldBeFalse();
        _gate.IsRestarting.ShouldBeFalse();
    }

    [Theory]
    [InlineData("/api/update/apply")]
    [InlineData("/api/update/rollout")]
    public async Task A_paired_phone_cannot_restart_fleet_or_start_a_rollout(string path)
    {
        await using var factory = Fleet();
        var (_, deviceToken) = await factory.Services.GetRequiredService<DeviceTokenService>().IssueAsync("Pixel", "android");
        using var phone = Client(factory, deviceToken);

        var response = await phone.PostAsync(path, content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        _gate.IsRestarting.ShouldBeFalse();
    }

    [Fact]
    public async Task The_machine_says_whether_it_can_restart_and_what_an_undone_update_was()
    {
        _launcher = false;
        File.WriteAllText(Path.Combine(_root, "update-failed.json"), """{"from":"0.59.0","to":"0.59.2","reason":"it exited with code 1 before it was ready","at":"2026-10-20T09:00:00Z"}""");
        await using var factory = Fleet();
        using var owner = Client(factory, OwnerToken(factory));

        var update = (await owner.GetFromJsonAsync<JsonElement>("/api/machine")).GetProperty("update");

        update.GetProperty("canRestart").GetBoolean().ShouldBeFalse("started by hand with an old launcher");
        update.GetProperty("restartUnavailableReason").GetString().ShouldNotBeNullOrEmpty();
        update.GetProperty("rolledBack").GetProperty("to").GetString().ShouldBe("0.59.2");
        update.GetProperty("rolledBack").GetProperty("reason").GetString()!.ShouldContain("exited with code 1");
    }

    private static async Task ConflictAsync(HttpClient owner, string message)
    {
        var response = await owner.PostAsync("/api/update/apply", content: null);
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString().ShouldBe(message);
    }

    private sealed class FakeBridgeTokens : IHarnessBridgeTokens
    {
        public bool IsKnown(string bridgeToken) => bridgeToken == BridgeToken;
    }

    private sealed class StubLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication()
        {
        }
    }
}

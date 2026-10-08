using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Tests.Auth;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Machines;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Testing.Builders;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Api.Tests.Endpoints;

/// <summary>
/// An automation on hangar that runs on falcon, another machine in hangar's list: Run now starts its session on falcon
/// through falcon's own API, and the run on hangar says so. With falcon away, the run is skipped and says why.
/// </summary>
[Collection(LocalTokenAuthServiceTestsGroup.Name)]
public sealed class AutomationMachineTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("fleet-automation-machine-").FullName;
    private readonly SessionOrchestratorBuilder _falconSessions = new SessionOrchestratorBuilder().WithUserContext(new TestUserContext("local-user"));

    public AutomationMachineTests()
    {
        _falconSessions.WorkspaceRootRepository.Seed(new WorkspaceRoot { Id = "root-1", Path = _folder, CreatedAt = "2026-10-08" });
        _falconSessions.ProjectRepository.Seed(new Project { Id = "scratch-1", Name = "Scratch", Type = "scratch", Position = 0, CreatedAt = "2026-10-08", UpdatedAt = "2026-10-08" });
        _falconSessions.RegisterHarness("opencode", "OpenCode").DefaultSession = new FakeHarnessSession("falcon-inst");
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task Run_now_starts_the_session_on_the_machine_and_the_run_says_where()
    {
        await using var falcon = Falcon();
        var route = new SwitchableHandler { Target = falcon.Server.CreateHandler() };
        await using var hangar = Hangar(route);
        await ListAsync(hangar, falcon);
        using var owner = Client(hangar, MachineToken(hangar));

        var created = await owner.PostAsJsonAsync("/api/automations", new
        {
            name = "Nightly check",
            prompt = "Check the build",
            triggerType = "schedule",
            triggerConfig = "0 2 * * *",
            workspaceId = _folder,
            isolation = "existing",
            targetMachineId = MachineId(falcon),
        });
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var automation = await created.Content.ReadFromJsonAsync<JsonElement>();
        automation.GetProperty("targetMachineId").GetString().ShouldBe(MachineId(falcon));
        var id = automation.GetProperty("id").GetString()!;

        var run = await RunNowAsync(owner, id);

        run.GetProperty("state").GetString().ShouldBe("done", run.GetRawText());
        run.GetProperty("machineId").GetString().ShouldBe(MachineId(falcon));
        run.GetProperty("machineName").GetString().ShouldBe("falcon");
        var sessionId = run.GetProperty("sessionId").GetString()!;
        var session = (await _falconSessions.SessionRepository.GetByIdAsync(sessionId)).ShouldNotBeNull("the session is on falcon");
        session.Title.ShouldBe("Automation: Nightly check");

        route.Down = true;
        var skipped = await RunNowAsync(owner, id);

        skipped.GetProperty("state").GetString().ShouldBe("skipped");
        skipped.GetProperty("error").GetString().ShouldBe("Skipped: falcon didn't answer.");
        skipped.GetProperty("machineName").GetString().ShouldBe("falcon");

        // The first run was done when falcon last said so, and it isn't asked again: it stays Done with falcon away.
        var runs = await owner.GetFromJsonAsync<JsonElement>($"/api/automations/{id}/runs");
        runs.GetProperty("runs").EnumerateArray().Single(r => r.GetProperty("id").GetString() == run.GetProperty("id").GetString())
            .GetProperty("state").GetString().ShouldBe("done");
    }

    /// <summary>Run now, then the run once it has finished starting.</summary>
    private static async Task<JsonElement> RunNowAsync(HttpClient owner, string automationId)
    {
        var started = await owner.PostAsync($"/api/automations/{automationId}/run", null);
        started.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var runId = (await started.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        for (var attempt = 0; attempt < 100; attempt++)
        {
            var runs = await owner.GetFromJsonAsync<JsonElement>($"/api/automations/{automationId}/runs");
            var run = runs.GetProperty("runs").EnumerateArray().First(r => r.GetProperty("id").GetString() == runId);
            if (run.GetProperty("state").GetString() != "starting")
                return run;
            await Task.Delay(100);
        }

        throw new TimeoutException("The run never finished starting.");
    }

    private static async Task ListAsync(WebApplicationFactory<Program> home, WebApplicationFactory<Program> other) =>
        await home.Services.GetRequiredService<RemoteMachineService>().ImportAsync(
            [new ImportedMachine(MachineId(other), "falcon", "https://falcon.test", MachineToken(other), "linux", null)]);

    private ApiWebApplicationFactory Falcon() => new(
        authEnabled: false,
        tokenAuthEnabled: true,
        simulateLocalhostRequest: true,
        host: "0.0.0.0",
        configureTestServices: services => services.AddSingleton(_ => _falconSessions.Build()));

    private static ApiWebApplicationFactory Hangar(HttpMessageHandler route) => new(
        authEnabled: false,
        tokenAuthEnabled: true,
        simulateLocalhostRequest: true,
        host: "0.0.0.0",
        configureTestServices: services =>
        {
            services.AddHttpClient(RemoteMachineService.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => route);
            services.AddHttpClient(RemoteAutomationRuns.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => route);
        });

    private sealed class SwitchableHandler : DelegatingHandler
    {
        public HttpMessageHandler? Target { get; set; }

        public bool Down { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Down
            ? throw new HttpRequestException("connection refused")
            : new HttpMessageInvoker(Target!, disposeHandler: false).SendAsync(request, cancellationToken);
    }

    private static string MachineToken(WebApplicationFactory<Program> factory) => factory.Services.GetRequiredService<ILocalTokenAuthService>().Token;

    private static string MachineId(WebApplicationFactory<Program> factory) => factory.Services.GetRequiredService<MachineIdentityStore>().Get().Id;

    private static HttpClient Client(WebApplicationFactory<Program> factory, string token)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}

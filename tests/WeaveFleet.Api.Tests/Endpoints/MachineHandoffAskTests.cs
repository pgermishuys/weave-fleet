using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Canvases;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Machines;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Machines;

namespace WeaveFleet.Api.Tests.Endpoints;

/// <summary>
/// An agent on hangar sends work to falcon, which is set to Ask: Fleet raises an ask in the agent's session, the call
/// waits, and the owner's answer through the permission endpoint lets it go on or not, and saves falcon's setting. Two
/// Fleets in one process, hangar's calls routed to falcon.
/// </summary>
[Collection(TwoFleetTestGroup.Name)]
public sealed class MachineHandoffAskTests
{
    private const string FalconUrl = "https://falcon.tail9c2e.ts.net";
    private const string BridgeToken = "bridge-1";
    private const string HarnessSessionId = "oc-1";
    private const string Asker = "sess-hangar";
    private const string Owner = "local-user";

    [Theory]
    [InlineData("always", "always")]
    [InlineData("reject", "never")]
    public async Task The_owners_answer_decides_the_start_and_saves_falcons_setting(string reply, string saved)
    {
        var sessionStarts = 0;
        await using var falcon = new ApiWebApplicationFactory(authEnabled: false, tokenAuthEnabled: true, simulateLocalhostRequest: true, host: "0.0.0.0");
        await using var hangar = new ApiWebApplicationFactory(
            authEnabled: false,
            tokenAuthEnabled: true,
            simulateLocalhostRequest: true,
            host: "0.0.0.0",
            configureTestServices: services =>
            {
                HttpMessageHandler ToFalcon() => new Counting(falcon.Server.CreateHandler(), request =>
                {
                    if (request is { Method.Method: "POST", RequestUri.AbsolutePath: "/api/sessions" })
                        Interlocked.Increment(ref sessionStarts);
                });
                services.AddHttpClient(RemoteMachineService.HttpClientName).ConfigurePrimaryHttpMessageHandler(ToFalcon);
                services.AddHttpClient(RemoteSessions.HttpClientName).ConfigurePrimaryHttpMessageHandler(ToFalcon);
                services.AddSingleton<IHarnessCanvasCallerResolver>(new Callers());
            });
        await SeedAsync(hangar);
        using var owner = Client(hangar, Token(hangar));
        var added = await (await owner.PostAsJsonAsync("/api/machines", new { baseUrl = FalconUrl, token = Token(falcon) })).ReadSuccessAsync();
        var falconId = added.GetProperty("id").GetString()!;
        var falconName = added.GetProperty("name").GetString()!;
        added.GetProperty("agentWork").GetString().ShouldBe("ask");

        // The relay's side, as when a harness process serves the session: what Fleet raises there.
        var raised = Channel.CreateUnbounded<HarnessEvent>();
        using var attached = hangar.Services.GetRequiredService<FleetSessionEvents>().Attach(Asker, raised.Writer);

        using var agent = Client(hangar, BridgeToken);
        var start = agent.PostAsJsonAsync("/api/bridge/session/start", new
        {
            harnessSessionId = HarnessSessionId, machine = falconName, folder = "/srv/harbor-api", title = "Run the tests", task = "Run them on Windows.",
        });
        var asked = AskedAsync(raised.Reader);
        if (await Task.WhenAny(asked, start) == start)
            throw new InvalidOperationException("The start didn't wait: " + await (await start).Content.ReadAsStringAsync());
        var ask = await asked;
        ask.Handoff!.Machine.ShouldBe(falconName);
        ask.Title.ShouldBe($"Start a session on {falconName}");
        start.IsCompleted.ShouldBeFalse("the call waits for the answer");
        sessionStarts.ShouldBe(0);

        var answered = await owner.PostAsJsonAsync($"/api/sessions/{Asker}/permissions/{ask.Id}", new { reply });
        var again = await owner.PostAsJsonAsync($"/api/sessions/{Asker}/permissions/{ask.Id}", new { reply });
        var result = await (await start.WaitAsync(TimeSpan.FromSeconds(20))).Content.ReadAsStringAsync();

        answered.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        again.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var machines = await owner.GetFromJsonAsync<JsonElement>("/api/machines");
        machines.GetProperty("machines").EnumerateArray().Single(m => m.GetProperty("id").GetString() == falconId)
            .GetProperty("agentWork").GetString().ShouldBe(saved);
        if (reply == "always")
        {
            result.ShouldNotContain("said no");
            sessionStarts.ShouldBe(1);
        }
        else
        {
            result.ShouldContain($"The user said no to sending work to {falconName}.");
            sessionStarts.ShouldBe(0);
        }
    }

    private static async Task<PermissionAsk> AskedAsync(ChannelReader<HarnessEvent> reader)
    {
        while (true)
        {
            var raised = await reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20));
            if (raised.Type == EventTypes.PermissionAsked)
                return raised.Payload!.Value.Deserialize<PermissionAsk>(WebJson)!;
        }
    }

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private static async Task SeedAsync(ApiWebApplicationFactory hangar)
    {
        using var scope = hangar.Services.CreateScope();
        using var connection = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        await connection.ExecuteAsync($"""
            INSERT INTO workspaces (id, directory, display_name, created_at, user_id)
            VALUES ('ws-1', '/ws', 'Work', '2026-11-07T00:00:00+00:00', '{Owner}');
            INSERT INTO instances (id, port, pid, directory, url, status, created_at, user_id)
            VALUES ('inst-1', 0, NULL, '/ws', '', 'running', '2026-11-07T00:00:00+00:00', '{Owner}');
            INSERT INTO sessions (
                id, workspace_id, instance_id, opencode_session_id, title, status, directory,
                lifecycle_status, retention_status, created_at, user_id)
            VALUES ('{Asker}', 'ws-1', 'inst-1', '{HarnessSessionId}', 'Ship the installer', 'active', '/ws',
                    'running', 'active', '2026-11-07T10:00:00+00:00', '{Owner}');
            """);
    }

    private static HttpClient Client(ApiWebApplicationFactory factory, string token)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static string Token(WebApplicationFactory<Program> factory) => factory.Services.GetRequiredService<ILocalTokenAuthService>().Token;

    private sealed class Callers : IHarnessCanvasCallerResolver
    {
        public Task<HarnessCanvasCaller?> ResolveAsync(string bridgeToken, string harnessSessionId, CancellationToken ct = default)
            => Task.FromResult(bridgeToken == BridgeToken && harnessSessionId == HarnessSessionId ? new HarnessCanvasCaller(Asker, Owner) : null);
    }

    private sealed class Counting(HttpMessageHandler inner, Action<HttpRequestMessage> seen) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            seen(request);
            return base.SendAsync(request, cancellationToken);
        }
    }
}

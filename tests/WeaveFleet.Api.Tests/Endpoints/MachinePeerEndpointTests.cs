using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WeaveFleet.Api.Endpoints;
using WeaveFleet.Api.Tests.Auth;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Devices;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Api.Tests.Endpoints;

/// <summary>
/// Another machine's Fleet talking to a session here for one of its agents (<c>/api/machine/peer</c>): only with this
/// machine's token, never from a phone, a browser or an agent on this machine. Delivery itself is
/// <see cref="SessionMessageDelivery"/>'s, covered in the Application tests; it needs a running harness.
/// </summary>
[Collection(LocalTokenAuthServiceTestsGroup.Name)]
#pragma warning disable CA1001 // Type owns disposable fields — disposal handled by IAsyncLifetime.DisposeAsync
public sealed class MachinePeerEndpointTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private const string BridgeToken = "bridge-1";
    private const string Session = "sess-installer";
    private const string Owner = "local-user";

    private readonly FakeSessionMessageProxy _messages = new();
    private ApiWebApplicationFactory? _factory;

    public async Task InitializeAsync()
    {
        _messages.GetMessagesBehavior = (_, _, _, _) => Task.FromResult(new MessagePage(
            [
                new HarnessMessage { Id = "m1", Role = "user", Parts = [new TextPart("Check the Windows installer.")], Timestamp = DateTimeOffset.UnixEpoch },
                new HarnessMessage { Id = "m2", Role = "assistant", Parts = [new TextPart("It installs and starts.")], Timestamp = DateTimeOffset.UnixEpoch },
            ],
            HasMore: false));

        _factory = new ApiWebApplicationFactory(
            authEnabled: false,
            tokenAuthEnabled: true,
            simulateLocalhostRequest: true,
            configureTestServices: services =>
            {
                services.AddSingleton<IHarnessBridgeTokens>(new FakeTokens());
                services.RemoveAll<ISessionMessageProxy>();
                services.AddScoped<ISessionMessageProxy>(_ => _messages);
            });
        await SeedSessionAsync();
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
            await _factory.DisposeAsync();
    }

    [Fact]
    public async Task The_machine_token_reads_a_page_as_fleet_session_read_shows_it()
    {
        using var machine = Client(MachineToken());

        var response = await machine.GetAsync($"/api/machine/peer/sessions/{Session}/page?limit=10");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("title").GetString().ShouldBe("Read Check the Windows installer");
        var text = body.GetProperty("text").GetString()!;
        text.ShouldStartWith($"Session \"Check the Windows installer\" ({Session}) · ");
        text.ShouldContain("It installs and starts.");
    }

    [Fact]
    public async Task A_session_that_is_not_here_is_not_found()
    {
        using var machine = Client(MachineToken());

        var page = await machine.GetAsync("/api/machine/peer/sessions/sess-missing/page");
        var message = await machine.PostAsJsonAsync("/api/machine/peer/sessions/sess-missing/message", Message());

        page.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ErrorAsync(page)).ShouldBe("No session sess-missing here.");
        message.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ErrorAsync(message)).ShouldBe("No session sess-missing here.");
    }

    [Fact]
    public async Task A_message_names_its_sender_or_is_refused_saying_which_field_is_missing()
    {
        using var machine = Client(MachineToken());

        var noMachine = await machine.PostAsJsonAsync($"/api/machine/peer/sessions/{Session}/message", Message() with { FromMachineId = " " });
        var noText = await machine.PostAsJsonAsync($"/api/machine/peer/sessions/{Session}/message", Message() with { Text = "" });
        var thisMachine = await machine.PostAsJsonAsync(
            $"/api/machine/peer/sessions/{Session}/message",
            Message() with { FromMachineId = _factory!.Services.GetRequiredService<MachineIdentityStore>().Get().Id });
        var badLimit = await machine.GetAsync($"/api/machine/peer/sessions/{Session}/page?limit=0");

        noMachine.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ErrorAsync(noMachine)).ShouldBe("fromMachineId is required, at most 64 characters.");
        noText.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ErrorAsync(noText)).ShouldBe("text is required.");
        thisMachine.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ErrorAsync(thisMachine)).ShouldBe("fromMachineId is this machine. A session here messages another with fleet_message.");
        badLimit.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_paired_phone_cannot_send_or_read()
    {
        var (_, deviceToken) = await _factory!.Services.GetRequiredService<DeviceTokenService>().IssueAsync("Pixel", "android");
        using var phone = Client(deviceToken);

        (await phone.PostAsJsonAsync($"/api/machine/peer/sessions/{Session}/message", Message())).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await phone.GetAsync($"/api/machine/peer/sessions/{Session}/page")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_agent_on_this_machine_cannot_name_a_sender_from_another_machine()
    {
        using var agent = _factory!.CreateClient();

        var response = await agent.PostAsJsonAsync($"/agent/{BridgeToken}/api/machine/peer/sessions/{Session}/message", Message());

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Loopback_without_the_token_cannot_either()
    {
        using var local = _factory!.CreateClient();

        var message = await local.PostAsJsonAsync($"/api/machine/peer/sessions/{Session}/message", Message());
        var page = await local.GetAsync($"/api/machine/peer/sessions/{Session}/page");

        message.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ErrorAsync(message)).ShouldBe(MachinePeerEndpoints.OnlyTheMachineTokenMessage);
        page.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_machine_says_it_takes_peer_messages()
    {
        using var machine = Client(MachineToken());

        var body = await machine.GetFromJsonAsync<JsonElement>("/api/machine");

        body.GetProperty("capabilities").GetProperty("peerMessages").GetBoolean().ShouldBeTrue();
    }

    private static PeerMessage Message() => new("m-atlas", "atlas", "s-sender", "Fix login flake", "Check the Windows installer on this branch.");

    private string MachineToken() => _factory!.Services.GetRequiredService<ILocalTokenAuthService>().Token;

    private HttpClient Client(string token)
    {
        var client = _factory!.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<string?> ErrorAsync(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();

    private async Task SeedSessionAsync()
    {
        using var scope = _factory!.Services.CreateScope();
        using var connection = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        await connection.ExecuteAsync($"""
            INSERT INTO workspaces (id, directory, display_name, created_at, user_id)
            VALUES ('ws-1', '/ws', 'Work', '2026-10-08T00:00:00+00:00', '{Owner}');
            INSERT INTO instances (id, port, pid, directory, url, status, created_at, user_id)
            VALUES ('inst-1', 0, NULL, '/ws', '', 'running', '2026-10-08T00:00:00+00:00', '{Owner}');
            INSERT INTO sessions (
                id, workspace_id, instance_id, opencode_session_id, title, status, directory,
                lifecycle_status, retention_status, created_at, user_id)
            VALUES ('{Session}', 'ws-1', 'inst-1', 'oc-1', 'Check the Windows installer', 'active', '/ws',
                    'running', 'active', '2026-10-08T10:00:00+00:00', '{Owner}');
            """);
    }

    // Sent with the web defaults, so the properties go as camelCase.
    private sealed record PeerMessage(string FromMachineId, string FromMachineName, string FromSessionId, string FromTitle, string Text);

    private sealed class FakeTokens : IHarnessBridgeTokens
    {
        public bool IsKnown(string bridgeToken) => bridgeToken == BridgeToken;
    }
}

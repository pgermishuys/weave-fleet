using System.Text.Json;
using System.Text.Json.Nodes;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.Acp;
using WeaveFleet.Infrastructure.Harnesses.Copilot;
using WeaveFleet.Infrastructure.Tests.Harnesses.Acp;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.Copilot;

public sealed class CopilotSignInTests
{
    private readonly List<FakeAcpAgent> _agents = [];
    private int _changed;
    private string _state = "pending";

    private CopilotSignIn Make(bool signedIn = true, string authType = "user")
    {
        return new CopilotSignIn(_ =>
        {
            var agent = new FakeAcpAgent();
            agent.Handle = (method, p) => Task.FromResult<JsonNode?>(method switch
            {
                "_fleet/auth/status" => new JsonObject { ["authenticated"] = signedIn, ["authType"] = authType, ["login"] = signedIn ? "octo-demo" : null },
                "_fleet/auth/login" => new JsonObject { ["attemptId"] = "a1", ["url"] = "https://github.com/login/device", ["code"] = "AB12-CD34", ["expiresAt"] = DateTimeOffset.UtcNow.AddMinutes(15).ToString("O") },
                "_fleet/auth/attempt" => new JsonObject { ["state"] = _state },
                _ => new JsonObject(),
            });
            _agents.Add(agent);
            return Task.FromResult<IAcpAgent>(agent);
        }, () => _changed++, TimeProvider.System);
    }

    [Fact]
    public async Task Signed_out_Copilot_lists_the_device_code_method_and_no_connection()
    {
        var provider = (await Make(signedIn: false).ListAsync("u", default)).Providers.Single();
        Assert.Equal("GitHub Copilot", provider.Name);
        Assert.Equal("device", provider.Methods.Single().Id);
        Assert.Empty(provider.Connections);
    }

    [Fact]
    public async Task Signed_in_Copilot_lists_who_and_an_environment_sign_in_cannot_be_signed_out_from_Fleet()
    {
        var own = (await Make().ListAsync("u", default)).Providers.Single().Connections.Single();
        Assert.Equal("Signed in as octo-demo", own.Label);
        Assert.Equal(HarnessSignInConnectionKinds.Credential, own.Kind);

        var env = (await Make(authType: "env").ListAsync("u", default)).Providers.Single().Connections.Single();
        Assert.Equal(HarnessSignInConnectionKinds.Environment, env.Kind);
    }

    [Fact]
    public async Task A_sign_in_shows_the_code_then_completes_and_lets_go_of_the_process()
    {
        var sut = Make(signedIn: false);
        var attempt = await sut.StartAsync("u", "github-copilot", "device", new Dictionary<string, JsonElement>(), default);
        Assert.Contains("AB12-CD34", attempt.Instructions);
        Assert.Equal("https://github.com/login/device", attempt.Url);
        Assert.Equal("pending", (await sut.GetAttemptAsync("u", "github-copilot", attempt.Id, default)).Status);

        _state = "complete";
        Assert.Equal("complete", (await sut.GetAttemptAsync("u", "github-copilot", attempt.Id, default)).Status);
        Assert.Equal(1, _changed);
        Assert.Equal("gone", (await sut.GetAttemptAsync("u", "github-copilot", attempt.Id, default)).Status);
    }

    [Fact]
    public async Task Cancelling_tells_weave_copilot_to_stop_and_releases_the_process()
    {
        var sut = Make(signedIn: false);
        var attempt = await sut.StartAsync("u", "github-copilot", "device", new Dictionary<string, JsonElement>(), default);
        await sut.CancelAsync("u", "github-copilot", attempt.Id, default);

        Assert.Contains(_agents[0].Received, r => r.Method == "_fleet/auth/cancel");
        Assert.False(_agents[0].IsRunning);
        Assert.Equal("gone", (await sut.GetAttemptAsync("u", "github-copilot", attempt.Id, default)).Status);
    }

    [Fact]
    public async Task Signing_out_asks_weave_copilot_to_remove_the_sign_in_and_a_key_is_refused()
    {
        var sut = Make();
        await sut.SignOutAsync("u", "github-copilot-login", default);
        Assert.Contains(_agents[0].Received, r => r.Method == "_fleet/auth/logout");
        Assert.Equal(1, _changed);
        await Assert.ThrowsAsync<HarnessSignInException>(() => sut.SignInWithKeyAsync("u", "github-copilot", "k", new Dictionary<string, JsonElement>(), default));
    }
}

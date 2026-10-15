using System.Text.Json.Nodes;
using WeaveFleet.Infrastructure.Harnesses.Acp;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.Acp;

public sealed class AcpClientTests
{
    [Fact]
    public async Task a_request_gets_its_own_result()
    {
        await using var agent = new FakeAcpAgent { Handle = (method, _) => Task.FromResult<JsonNode?>(new JsonObject { ["echo"] = method }) };

        var result = await agent.Client.RequestAsync("initialize", new JsonObject { ["protocolVersion"] = 1 }, CancellationToken.None);

        result.GetProperty("echo").GetString().ShouldBe("initialize");
        agent.Received.Single().Params.GetProperty("protocolVersion").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task an_error_response_becomes_an_exception_with_the_agents_words()
    {
        await using var agent = new FakeAcpAgent { Handle = (_, _) => throw new InvalidOperationException("fusion routing transport failed") };

        var failure = await Should.ThrowAsync<AcpException>(() => agent.Client.RequestAsync("session/prompt", null, CancellationToken.None));

        failure.Message.ShouldBe("fusion routing transport failed");
    }

    [Fact]
    public async Task notifications_arrive_in_order_and_before_the_response_that_follows_them()
    {
        var seen = new List<string>();
        await using var agent = new FakeAcpAgent();
        agent.Client.Notification += (_, p) => seen.Add(p.GetProperty("update").GetProperty("n").GetString()!);
        agent.Handle = (_, _) =>
        {
            agent.Update("s", new JsonObject { ["n"] = "one" });
            agent.Update("s", new JsonObject { ["n"] = "two" });
            return Task.FromResult<JsonNode?>(new JsonObject());
        };

        await agent.Client.RequestAsync("session/prompt", null, CancellationToken.None);

        seen.ShouldBe(["one", "two"]);
    }

    [Fact]
    public async Task a_request_from_the_agent_is_answered_without_blocking_the_reading()
    {
        await using var agent = new FakeAcpAgent();
        var release = new TaskCompletionSource();
        agent.Client.RequestHandler = async (_, _) =>
        {
            await release.Task;
            return new JsonObject { ["outcome"] = "yes" };
        };

        var asked = agent.AskAsync("session/request_permission", new JsonObject());
        var other = await agent.Client.RequestAsync("ping", null, CancellationToken.None); // the reading carries on while the ask waits
        release.SetResult();

        other.ValueKind.ShouldNotBe(System.Text.Json.JsonValueKind.Undefined);
        (await asked).GetProperty("outcome").GetString().ShouldBe("yes");
    }

    [Fact]
    public async Task requests_pending_when_the_agent_dies_fail_instead_of_hanging()
    {
        await using var agent = new FakeAcpAgent { Handle = (_, _) => new TaskCompletionSource<JsonNode?>().Task };
        var pending = agent.Client.RequestAsync("session/prompt", null, CancellationToken.None);

        agent.Die();

        (await Should.ThrowAsync<AcpException>(pending)).Message.ShouldContain("stopped");
    }
}

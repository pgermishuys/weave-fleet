using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Canvases;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode2;

/// <summary>How a Fleet tool call from an OpenCode 2 server finds its Fleet session.</summary>
public sealed class OpenCode2CanvasCallerResolverTests
{
    private const string Token = "token-a";

    [Fact]
    public async Task A_call_from_an_attached_session_belongs_to_its_Fleet_session_and_owner()
    {
        await using var server = Server(Token);
        await using var session = Attach(server, "ses_parent", "fleet-1", "owner-1");

        var caller = await Resolver(server).ResolveAsync(Token, "ses_parent");

        caller.ShouldBe(new HarnessCanvasCaller("fleet-1", "owner-1"));
    }

    [Theory]
    [InlineData("", "ses_parent")]
    [InlineData("token-unknown", "ses_parent")]
    [InlineData(Token, "")]
    public async Task A_call_Fleet_cannot_place_resolves_to_nothing(string token, string sessionId)
    {
        await using var server = Server(Token);
        await using var session = Attach(server, "ses_parent", "fleet-1", "owner-1");

        (await Resolver(server).ResolveAsync(token, sessionId)).ShouldBeNull();
    }

    [Fact]
    public async Task A_session_attached_to_another_server_is_not_this_servers_to_use()
    {
        await using var mine = Server(Token);
        await using var other = Server("token-b");
        await using var session = Attach(other, "ses_other", "fleet-2", "owner-2");
        var resolver = new OpenCode2CanvasCallerResolver(
            token => token == Token ? mine : token == "token-b" ? other : null, NullLogger.Instance);

        (await resolver.ResolveAsync(Token, "ses_other")).ShouldBeNull();
        (await resolver.ResolveAsync("token-b", "ses_other")).ShouldBe(new HarnessCanvasCaller("fleet-2", "owner-2"));
    }

    [Fact]
    public async Task A_subagents_call_belongs_to_the_session_that_started_it()
    {
        await using var server = Server(Token, Parents(("ses_grandchild", "ses_child"), ("ses_child", "ses_parent")));
        await using var session = Attach(server, "ses_parent", "fleet-1", "owner-1");

        (await Resolver(server).ResolveAsync(Token, "ses_grandchild")).ShouldBe(new HarnessCanvasCaller("fleet-1", "owner-1"));
    }

    [Fact]
    public async Task The_parent_chain_is_followed_only_so_far()
    {
        await using var server = Server(Token, Parents(
            ("ses_4", "ses_3"), ("ses_3", "ses_2"), ("ses_2", "ses_1"), ("ses_1", "ses_parent")));
        await using var session = Attach(server, "ses_parent", "fleet-1", "owner-1");

        OpenCode2CanvasCallerResolver.MaxParentHops.ShouldBe(3);
        (await Resolver(server).ResolveAsync(Token, "ses_1")).ShouldNotBeNull();
        (await Resolver(server).ResolveAsync(Token, "ses_4")).ShouldBeNull();
    }

    [Fact]
    public async Task A_session_without_a_parent_or_one_V2_does_not_know_resolves_to_nothing()
    {
        await using var server = Server(Token, Parents(("ses_orphan", null)));
        await using var session = Attach(server, "ses_parent", "fleet-1", "owner-1");

        (await Resolver(server).ResolveAsync(Token, "ses_orphan")).ShouldBeNull();
        (await Resolver(server).ResolveAsync(Token, "ses_missing")).ShouldBeNull();
    }

    [Fact]
    public async Task A_failed_parent_lookup_resolves_to_nothing()
    {
        await using var server = new OpenCode2Server(
            "owner-1",
            OpenCode2Fixtures.ClientServing("", new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError))),
            Token,
            process: null,
            NullLogger.Instance);
        await using var session = Attach(server, "ses_parent", "fleet-1", "owner-1");

        (await Resolver(server).ResolveAsync(Token, "ses_child")).ShouldBeNull();
    }

    private static OpenCode2CanvasCallerResolver Resolver(OpenCode2Server server)
        => new(token => token == server.BridgeToken ? server : null, NullLogger.Instance);

    private static OpenCode2Server Server(string token, StubHandler? api = null)
        => new("owner-1", OpenCode2Fixtures.ClientServing("", api), token, process: null, NullLogger.Instance);

    /// <summary>A V2 API that knows each session's parent; other sessions are 404.</summary>
    private static StubHandler Parents(params (string Session, string? Parent)[] sessions)
        => new(request =>
        {
            var id = request.RequestUri!.AbsolutePath.Split('/')[^1];
            if (sessions.FirstOrDefault(s => s.Session == id) is not { Session: not null } known)
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            var parent = known.Parent is null ? "" : $",\"parentID\":\"{known.Parent}\"";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"data\":{{\"id\":\"{id}\"{parent}}}}}", Encoding.UTF8, "application/json"),
            };
        });

    private static OpenCode2HarnessSession Attach(OpenCode2Server server, string harnessSessionId, string fleetSessionId, string owner)
        => new(
            "opencode2-test",
            new OpenCode2SessionInfo { Id = harnessSessionId },
            new OpenCode2SessionContext(fleetSessionId, owner, "/work", null, null),
            server,
            _ => Task.FromResult(server),
            analytics: null,
            delegations: null,
            NullLogger<OpenCode2HarnessSession>.Instance);
}

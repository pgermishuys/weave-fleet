using System.Text.Json;
using WeaveFleet.Infrastructure.Harnesses.Copilot;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.Copilot;

public sealed class CopilotFleetToolsTests
{
    [Fact]
    public void Fleets_server_is_an_http_server_whose_token_is_in_the_Authorization_header_not_the_url()
    {
        var servers = CopilotFleetTools.McpServers("http://127.0.0.1:6262/", "tok-1");

        var fleet = servers.Single()!;
        fleet["type"]!.GetValue<string>().ShouldBe("http");
        fleet["name"]!.GetValue<string>().ShouldBe("fleet");
        fleet["url"]!.GetValue<string>().ShouldBe("http://127.0.0.1:6262/mcp");
        var header = fleet["headers"]!.AsArray().Single()!;
        (header["name"]!.GetValue<string>(), header["value"]!.GetValue<string>()).ShouldBe(("Authorization", "Bearer tok-1"));
    }

    [Fact]
    public void Without_Fleets_address_there_are_no_servers()
        => CopilotFleetTools.McpServers(null, "tok-1").ShouldBeEmpty();

    [Fact]
    public async Task A_token_names_its_session_and_stops_working_when_revoked()
    {
        var tools = new CopilotFleetTools();
        var token = tools.Issue("sess-1", "owner");

        new CopilotBridgeTokens(tools).IsKnown(token).ShouldBeTrue();
        (await new CopilotCanvasCallerResolver(tools).ResolveAsync(token, "", CancellationToken.None))!.FleetSessionId.ShouldBe("sess-1");
        (await new CopilotMcpCalls(tools).PlaceAsync(token, default, CancellationToken.None))!.HarnessSessionId.ShouldBe("sess-1");

        tools.Revoke(token);
        new CopilotBridgeTokens(tools).IsKnown(token).ShouldBeFalse();
        (await new CopilotMcpCalls(tools).PlaceAsync(token, default, CancellationToken.None)).ShouldBeNull();
    }
}

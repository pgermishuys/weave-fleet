using System.Net;
using System.Text;
using System.Text.Json;
using WeaveFleet.Api;

namespace WeaveFleet.Api.Tests;

/// <summary>
/// <c>fleet mcp</c>: the MCP server Claude Code starts with a terminal session. It must always answer the handshake, say
/// nothing else on stdout, and tell the Fleet on this machine when the session starts and when it ends.
/// </summary>
public sealed class TerminalMcpCommandTests
{
    private const string Initialize = """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"claude-code","version":"2.1.329"}}}""";
    private const string Initialized = """{"jsonrpc":"2.0","method":"notifications/initialized"}""";
    private const string ListTools = """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""";

    private static readonly Dictionary<string, string> Environment = new()
    {
        ["CLAUDE_CODE_SESSION_ID"] = "0b6f1c1e-2a2d-4f4e-9a55-1d3c4e5f6a70",
        ["CLAUDE_PROJECT_DIR"] = "/work/orchard",
    };

    private static async Task<(int Exit, List<string> Output, Handler Fleet)> RunAsync(
        string input, Handler? fleet = null, string? token = "machine-token-0123456789", Dictionary<string, string>? environment = null, string cwd = "/work/orchard")
    {
        fleet ??= new Handler();
        using var http = new HttpClient(fleet);
        var output = new StringWriter();
        var env = environment ?? Environment;
        var exit = await TerminalMcpCommand.RunAsync(
            "0.0.0.0", 2113, Path.Combine(Path.GetTempPath(), $"fleet-{Guid.NewGuid():N}.db"), token, "1.2.3", http,
            new StringReader(input), output, key => env.GetValueOrDefault(key), cwd);
        return (exit, [.. output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)], fleet);
    }

    [Fact]
    public async Task It_answers_the_handshake_and_lists_no_tools_and_prints_nothing_else()
    {
        var (exit, output, _) = await RunAsync(string.Join('\n', Initialize, Initialized, ListTools) + "\n");

        exit.ShouldBe(0);
        output.Count.ShouldBe(2);
        using var initialize = JsonDocument.Parse(output[0]);
        initialize.RootElement.GetProperty("id").GetInt32().ShouldBe(0);
        initialize.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString().ShouldBe("2025-11-25");
        initialize.RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString().ShouldBe("fleet");
        using var tools = JsonDocument.Parse(output[1]);
        tools.RootElement.GetProperty("result").GetProperty("tools").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task It_tells_Fleet_the_session_started_and_ended_over_loopback_with_the_machine_token()
    {
        var (_, _, fleet) = await RunAsync(Initialize + "\n");

        fleet.Requests.Count.ShouldBe(2);
        fleet.Requests.ShouldAllBe(r => r.Url == "http://127.0.0.1:2113/api/machine/terminal-sessions");
        fleet.Requests.ShouldAllBe(r => r.Authorization == "Bearer machine-token-0123456789");
        var (started, ended) = (JsonDocument.Parse(fleet.Requests[0].Body).RootElement, JsonDocument.Parse(fleet.Requests[1].Body).RootElement);
        started.GetProperty("event").GetString().ShouldBe("started");
        ended.GetProperty("event").GetString().ShouldBe("ended");
        started.GetProperty("sessionId").GetString().ShouldBe("0b6f1c1e-2a2d-4f4e-9a55-1d3c4e5f6a70");
        started.GetProperty("folder").GetString().ShouldBe("/work/orchard");
    }

    [Fact]
    public async Task It_still_answers_when_no_Fleet_answers()
    {
        var (exit, output, _) = await RunAsync(Initialize + "\n", new Handler(throws: true));

        exit.ShouldBe(0);
        output.ShouldHaveSingleItem().ShouldContain("\"serverInfo\"");
    }

    [Fact]
    public async Task It_still_answers_with_no_machine_token_and_reports_nothing()
    {
        var (exit, output, fleet) = await RunAsync(Initialize + "\n", token: null);

        exit.ShouldBe(0);
        output.Count.ShouldBe(1);
        fleet.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_request_it_has_no_answer_for_gets_an_error_and_a_notification_gets_none()
    {
        var (_, output, _) = await RunAsync("""{"jsonrpc":"2.0","id":7,"method":"resources/list"}""" + "\n" + Initialized + "\nnot json\n");

        var answer = output.ShouldHaveSingleItem();
        using var document = JsonDocument.Parse(answer);
        document.RootElement.GetProperty("id").GetInt32().ShouldBe(7);
        document.RootElement.GetProperty("error").GetProperty("code").GetInt32().ShouldBe(-32601);
    }

    [Fact]
    public async Task Without_a_session_id_it_names_the_newest_transcript_in_the_folder_or_nothing()
    {
        var (_, _, fleet) = await RunAsync(Initialize + "\n", environment: [], cwd: "/work/no-such-folder");

        var started = JsonDocument.Parse(fleet.Requests[0].Body).RootElement;
        started.GetProperty("folder").GetString().ShouldBe("/work/no-such-folder");
        started.GetProperty("sessionId").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task While_the_session_is_open_it_sends_a_heartbeat_and_a_kill_without_a_goodbye_leaves_only_those()
    {
        var fleet = new Handler();
        using var http = new HttpClient(fleet);
        var input = new WaitingReader();
        var run = TerminalMcpCommand.RunAsync(
            "0.0.0.0", 2113, Path.Combine(Path.GetTempPath(), $"fleet-{Guid.NewGuid():N}.db"), "machine-token-0123456789", "1.2.3", http,
            input, new StringWriter(), key => Environment.GetValueOrDefault(key), "/work/orchard", TimeSpan.FromMilliseconds(30));

        await Task.Delay(400);
        var kinds = fleet.Requests.Select(r => JsonDocument.Parse(r.Body).RootElement.GetProperty("event").GetString()).ToList();
        input.End();
        (await run).ShouldBe(0);

        kinds[0].ShouldBe("started");
        kinds.Count(k => k == "heartbeat").ShouldBeGreaterThan(2);
        kinds.ShouldNotContain("ended");
    }

    /// <summary>An input that stays open until it's closed, as Claude Code's does while the session runs.</summary>
    private sealed class WaitingReader : TextReader
    {
        private readonly TaskCompletionSource _closed = new();

        public void End() => _closed.TrySetResult();

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            await _closed.Task.WaitAsync(cancellationToken);
            return null;
        }

        public override Task<string?> ReadLineAsync() => ReadLineAsync(CancellationToken.None).AsTask();
    }

    private sealed class Handler(bool throws = false) : HttpMessageHandler
    {
        public List<(string Url, string? Authorization, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (throws)
                throw new HttpRequestException("Connection refused");
            Requests.Add((request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), await request.Content!.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        }
    }
}

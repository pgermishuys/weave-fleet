using System.Net;
using System.Text;
using WeaveFleet.Api;

namespace WeaveFleet.Api.Tests;

/// <summary><c>fleet node pair</c>: asks the node on this machine for a code and prints it with the address.</summary>
public sealed class NodePairCommandTests
{
    private const string Answer = """{"manualCode":"7K3M-Q9ZD","expiresAt":"2026-10-24T10:10:00Z","machineId":"m1","machineName":"falcon","addresses":["http://100.64.90.72:2113","http://falcon:2113"]}""";

    [Fact]
    public async Task It_prints_the_code_and_the_addresses_and_signs_with_the_token_from_the_environment()
    {
        var handler = new Handler(HttpStatusCode.OK, Answer);
        var (exit, output, error) = await RunAsync(handler, "0.0.0.0", "env-token-0123456789");

        exit.ShouldBe(0, error);
        output.ShouldContain("7K3M-Q9ZD");
        output.ShouldContain("http://100.64.90.72:2113");
        output.ShouldContain("http://falcon:2113");
        output.ShouldContain("10 minutes");
        handler.Url.ShouldBe("http://127.0.0.1:2113/api/machine/pairing/code");
        handler.Authorization.ShouldBe("Bearer env-token-0123456789");
    }

    [Fact]
    public async Task It_reads_the_saved_machine_token_when_the_environment_has_none()
    {
        var handler = new Handler(HttpStatusCode.OK, Answer);
        var db = Path.Combine(Path.GetTempPath(), $"fleet-{Guid.NewGuid():N}.db");

        var (exit, _, _) = await RunAsync(handler, "127.0.0.1", null, db);

        exit.ShouldBe(0);
        var saved = new WeaveFleet.Application.Configuration.MachineIdentityStore(db).Get().AccessToken;
        handler.Authorization.ShouldBe($"Bearer {saved}");
    }

    [Fact]
    public async Task With_no_reachable_address_it_gives_the_local_one()
    {
        var handler = new Handler(HttpStatusCode.OK, Answer.Replace("\"http://100.64.90.72:2113\",\"http://falcon:2113\"", ""));
        var (_, output, _) = await RunAsync(handler, "127.0.0.1", "env-token-0123456789");

        output.ShouldContain("http://localhost:2113");
    }

    [Fact]
    public async Task No_node_running_says_to_start_it()
    {
        var (exit, output, error) = await RunAsync(new Handler(throws: true), "127.0.0.1", "env-token-0123456789");

        exit.ShouldBe(1);
        output.ShouldBeEmpty();
        error.ShouldContain("Start the node first");
    }

    [Fact]
    public async Task A_refusal_says_to_use_the_same_settings_as_the_node()
    {
        var (exit, _, error) = await RunAsync(new Handler(HttpStatusCode.Unauthorized, "{}"), "127.0.0.1", "wrong-token-0123456789");

        exit.ShouldBe(1);
        error.ShouldContain("401");
        error.ShouldContain("--data-dir");
    }

    private static async Task<(int Exit, string Output, string Error)> RunAsync(Handler handler, string host, string? token, string? db = null)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        using var http = new HttpClient(handler);
        var exit = await NodePairCommand.RunAsync(host, 2113, db ?? Path.Combine(Path.GetTempPath(), $"fleet-{Guid.NewGuid():N}.db"), token, http, output, error);
        return (exit, output.ToString(), error.ToString());
    }

    private sealed class Handler(HttpStatusCode status = HttpStatusCode.OK, string body = "", bool throws = false) : HttpMessageHandler
    {
        public string? Url { get; private set; }
        public string? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (throws)
                throw new HttpRequestException("Connection refused");
            Url = request.RequestUri!.ToString();
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}

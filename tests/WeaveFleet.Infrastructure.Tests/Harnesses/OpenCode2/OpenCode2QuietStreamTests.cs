using System.IO.Pipelines;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode2;

/// <summary>
/// The event stream stays open but the event that ends a turn never comes (V2 or a proxy dropped it). V2's own status
/// says the session is idle, so Fleet's session must not stay Working for ever.
/// </summary>
public sealed class OpenCode2QuietStreamTests
{
    private const string Session = "ses_f35d772d0ffe5Nudf5Dt2Eb6ty";

    [Fact]
    public async Task A_turn_whose_end_event_never_arrives_on_an_open_stream_ends_when_V2_says_the_session_is_idle()
    {
        var stream = new Pipe();
        var activeRead = 0;
        var api = new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/session/active")
            {
                Interlocked.Increment(ref activeRead);
                return Json("""{"data":{}}""");
            }

            return request.RequestUri.AbsolutePath.EndsWith("/message", StringComparison.Ordinal)
                ? Json("""{"data":[]}""")
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var client = new OpenCode2HttpClient(
            new HttpClient(api) { BaseAddress = new Uri("http://127.0.0.1:1/") },
            new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream.Reader.AsStream()) { Headers = { ContentType = new("text/event-stream") } },
            })) { BaseAddress = new Uri("http://127.0.0.1:1/") },
            NullLogger<OpenCode2HttpClient>.Instance);
        await using var server = new OpenCode2Server("local-user", client, "token", process: null, NullLogger.Instance) { QuietCheckAfter = TimeSpan.FromMilliseconds(300) };
        await using var session = new OpenCode2HarnessSession(
            "opencode2-test",
            new OpenCode2SessionInfo { Id = Session },
            new OpenCode2SessionContext("fleet-session-1", "local-user", "/work/proj", null, null),
            server,
            _ => Task.FromResult(server),
            analytics: null,
            NullLogger.Instance);

        await Write(stream, """{"id":"evt_1","type":"server.connected","data":{}}""");
        await Write(stream, $$$"""{"id":"evt_2","type":"session.execution.started","data":{"sessionID":"{{{Session}}}"}}""");
        await WaitFor(() => session.Status == HarnessSessionStatus.Running);

        // Nothing more is sent: the stream is open and quiet, and V2 says nothing runs.
        await WaitFor(() => session.Status == HarnessSessionStatus.Idle, TimeSpan.FromSeconds(10));

        session.Status.ShouldBe(HarnessSessionStatus.Idle);
        activeRead.ShouldBeGreaterThan(0);
    }

    private static async Task Write(Pipe pipe, string json)
        => await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes($"data: {json}\n\n"));

    private static async Task WaitFor(Func<bool> condition, TimeSpan? limit = null)
    {
        var until = DateTime.UtcNow + (limit ?? TimeSpan.FromSeconds(5));
        while (!condition() && DateTime.UtcNow < until)
            await Task.Delay(50);
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };
}

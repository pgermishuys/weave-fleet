using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode2;

/// <summary>
/// Event streams recorded from a real OpenCode 2 (2.0.6) server against a scripted model:
/// <list type="bullet">
///   <item><c>text-and-tool-turn.sse</c>: one session, a text turn ("say hello") then a shell tool turn.</item>
///   <item><c>permission-form-subagent-interrupt.sse</c>: five sessions (a plugin tool, a permission ask, a form,
///   a subagent, and a turn interrupted mid-tool).</item>
/// </list>
/// </summary>
internal static class OpenCode2Fixtures
{
    public const string TextSession = "ses_f49c46e4fffenc9y2xgW2cMr7e";
    public const string TextTurnMessage = "msg_0b63b955c001u44eHxDgFRDgyU";
    public const string PluginToolSession = "ses_f49befceaffes0ozgN8Z9W2zu1";
    public const string PermissionSession = "ses_f49beeb44ffenV4MvaJHFFQzs4";
    public const string FormSession = "ses_f49becf54ffeEaine7WWKSPOgM";
    public const string SubagentSession = "ses_f49bebf2bffe2EwiUcF3yeqrZO";
    public const string InterruptedSession = "ses_f49be8651ffeWEBnoQ195zcSsj";

    public static string Read(string fileName)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "OpenCode2", fileName));

    /// <summary>Reads a recording through the real client, the way Fleet reads a live server's stream.</summary>
    public static async Task<List<OpenCode2Event>> ReadEventsAsync(string fileName)
    {
        using var client = ClientServing(Read(fileName));
        var events = new List<OpenCode2Event>();
        await foreach (var evt in client.ReadEventsAsync(() => { }, CancellationToken.None))
            events.Add(evt);
        return events;
    }

    /// <summary>A client whose event stream is <paramref name="sse"/> and whose other requests go to <paramref name="api"/>.</summary>
    public static OpenCode2HttpClient ClientServing(string sse, HttpMessageHandler? api = null)
        => new(
            new HttpClient(api ?? new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))) { BaseAddress = new Uri("http://127.0.0.1:1/") },
            new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
            })) { BaseAddress = new Uri("http://127.0.0.1:1/") },
            NullLogger<OpenCode2HttpClient>.Instance);
}

/// <summary>Answers every request with <paramref name="respond"/> and keeps what was sent.</summary>
internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpMethod Method, string Path, string? Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (Requests)
            Requests.Add((request.Method, request.RequestUri!.AbsolutePath, body));
        return respond(request);
    }
}

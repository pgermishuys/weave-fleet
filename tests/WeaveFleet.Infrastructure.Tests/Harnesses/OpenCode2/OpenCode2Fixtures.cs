using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode2;

/// <summary>
/// Event streams recorded from a real OpenCode 2 (2.0.6) server against a scripted model:
/// <list type="bullet">
///   <item><c>text-and-tool-turn.sse</c>: one session, a text turn ("say hello") then a shell tool turn.</item>
///   <item><c>permission-form-subagent-interrupt.sse</c>: five sessions (a plugin tool, a permission ask, a form,
///   a subagent, and a turn interrupted mid-tool).</item>
///   <item><c>tools-questions.sse</c> (2.0.8): one session created allowing everything, four turns: a shell call, a
///   read of a missing file (the tool fails), a question answered "B", and a question dismissed (V2 then interrupts
///   the turn). <c>tools-questions.messages.json</c> is the same session's history afterwards
///   (<c>GET /api/session/{id}/message</c>), and <c>.messages-page1/2.json</c> the same read three at a time.
///   <c>tools-questions.forms.json</c> is <c>GET /api/session/{id}/form</c> while the first question waited.</item>
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

    public const string ToolsSession = "ses_f48064e63ffe2g85VM7MArlHhg";
    public const string ShellMessage = "msg_0b7f9b532001IWxxG4F1WnlueS";
    public const string ShellCall = "call_1789792990530";
    public const string FailedReadMessage = "msg_0b7f9c4d9001GVrNU6kImg9WlD";
    public const string FailedReadCall = "call_1789792994543";
    public const string AnsweredQuestionMessage = "msg_0b7f9d4a5001x2NlXtf50JvuU9";
    public const string AnsweredQuestionCall = "call_1789792998586";
    public const string AnsweredQuestionForm = "frm_0b7f9d4cb001hPPwPu8NuxQJaV";
    public const string DismissedQuestionCall = "call_1789793005718";

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

    /// <summary>
    /// A server's answers to what runs on it: <paramref name="active"/> for <c>GET /api/session/active</c>, the folders
    /// of <paramref name="shells"/> for <c>GET /api/debug/location</c>, and each folder's shells (<see cref="Shell"/>)
    /// for <c>GET /api/shell?location[directory]=…</c>. Anything else is a 404.
    /// </summary>
    public static StubHandler Running(string active = """{"data":{}}""", IReadOnlyDictionary<string, string[]>? shells = null)
    {
        var folders = shells ?? new Dictionary<string, string[]>();
        return new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/session/active" => Json(active),
            "/api/debug/location" => Json(JsonSerializer.Serialize(folders.Keys.Select(directory => new { directory }))),
            "/api/shell" when System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["location[directory]"] is { } directory
                && folders.TryGetValue(directory, out var listed)
                => Json($$"""{"location":{"directory":{{JsonSerializer.Serialize(directory)}}},"data":[{{string.Join(',', listed)}}]}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
    }

    /// <summary>One <c>Shell.Info</c> as V2 (2.0.9) lists it: a shell call moved to the background by session <paramref name="sessionId"/>.</summary>
    public static string Shell(string status, string sessionId = "ses_a", string id = "sh_0cb185fae0014nGWz6OlKu46xL")
        => $$$"""
            {"id":"{{{id}}}","status":"{{{status}}}","command":"sleep 25; echo bg-done","cwd":"/work","shell":"/usr/bin/bash",
             "file":"/home/you/.local/share/opencode/shell/8c95/{{{id}}}.out","pid":157202,"metadata":{"sessionID":"{{{sessionId}}}"},
             "time":{"started":1790113760000}}
            """;

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };
}

/// <summary>Answers every request with <paramref name="respond"/> and keeps what was sent.</summary>
internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpMethod Method, string Path, string? Body)> Requests { get; } = [];

    /// <summary>Every request's whole address, query included.</summary>
    public List<string> Uris { get; } = [];

    /// <summary>Runs for every request after it's recorded, before it's answered: a server's side effects.</summary>
    public Action<HttpRequestMessage>? OnRequest { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (Requests)
        {
            Requests.Add((request.Method, request.RequestUri!.AbsolutePath, body));
            Uris.Add(request.RequestUri.ToString());
        }
        OnRequest?.Invoke(request);
        return respond(request);
    }
}

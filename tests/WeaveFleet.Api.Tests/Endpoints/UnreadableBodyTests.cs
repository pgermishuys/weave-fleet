using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using WeaveFleet.Api.Tests.Infrastructure;

namespace WeaveFleet.Api.Tests.Endpoints;

/// <summary>
/// A body Fleet can't read used to get a 400 with nothing in it, so an agent calling the API had to guess what was
/// wrong. These are the bodies an agent is likely to send by mistake, some of them as Windows PowerShell builds them.
/// </summary>
[Collection("NonParallelApiFactoryTests")]
public sealed class UnreadableBodyTests : IDisposable
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    private readonly ApiWebApplicationFactory _factory = new(authEnabled: false);
    private readonly HttpClient _client;

    public UnreadableBodyTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task UnknownField_NamesItAndListsTheFieldsTheEndpointTakes()
    {
        var (status, error) = await PostAsync("/api/sessions", """{"directory":"/tmp/x","prompt":"Fix the build"}""");

        status.ShouldBe(HttpStatusCode.BadRequest);
        error.ShouldStartWith("Unknown field \"prompt\". Fields this endpoint takes: directory, title, ");
        error.ShouldContain("initialPrompt");
        error.ShouldNotContain("Fix the build");
    }

    [Fact]
    public async Task Answer_ReadsPlainlyInRawJson()
    {
        using var content = new StringContent("""{"prompt":"x"}""", Encoding.UTF8, "application/json");

        var response = await _client.PostAsync("/api/sessions", content);
        var text = await response.Content.ReadAsStringAsync();

        // An agent reading curl's output sees the quotes, not \u0022.
        text.ShouldStartWith("""{"error":"Unknown field \"prompt\". Fields this endpoint takes: """);
    }

    [Fact]
    public async Task UnknownFieldInsideAnObject_SaysWhichObject()
    {
        var (status, error) = await PostAsync("/api/sessions", """{"onComplete":{"notifySessionId":"a","sessionId":"b"}}""");

        status.ShouldBe(HttpStatusCode.BadRequest);
        error.ShouldBe("Unknown field \"sessionId\" in \"onComplete\". Fields it takes: notifySessionId, notifyInstanceId.");
    }

    [Theory]
    [InlineData("""{"tags":"urgent"}""", "\"tags\" should be a list of strings.")]
    // Windows PowerShell 5.1's ConvertTo-Json can wrap an array as {"value":[…],"Count":n}.
    [InlineData("""{"tags":{"value":["urgent"],"Count":1}}""", "\"tags\" should be a list of strings.")]
    [InlineData("""{"tags":["urgent",1]}""", "\"tags[1]\" should be a string.")]
    [InlineData("""{"title":42}""", "\"title\" should be a string.")]
    // ConvertTo-Json's default -Depth 2 writes anything deeper as the object's type name.
    [InlineData("""{"source":{"key":"System.Collections.Hashtable","input":"System.Collections.Hashtable"}}""",
        "\"source.key\" should be an object with these fields: providerId (required), sourceType (required), actionId (required), contractVersion.")]
    [InlineData("""{"source":{"input":{}}}""", "\"source\" should be an object with these fields: key (required), input (required).")]
    [InlineData("""["/tmp/x"]""", "The body should be a JSON object with these fields: directory, title, ")]
    public async Task WrongType_SaysWhatTheFieldShouldBe(string body, string expected)
    {
        var (status, error) = await PostAsync("/api/sessions", body);

        status.ShouldBe(HttpStatusCode.BadRequest);
        error.ShouldStartWith(expected);
    }

    [Theory]
    [InlineData("""{"directory":"/tmp/x",}""", "The body isn't valid JSON (line 1, byte 23): ")]
    [InlineData("""{"directory":"/tmp/x","title":"Dry run""", "The body isn't valid JSON (line 1, byte 39): ")]
    [InlineData("{'directory':'/tmp/x'}", "The body isn't valid JSON (line 1, byte 2): ")]
    public async Task InvalidJson_SaysWhereItStopsBeingJson(string body, string expected)
    {
        var (status, error) = await PostAsync("/api/sessions", body);

        status.ShouldBe(HttpStatusCode.BadRequest);
        error.ShouldStartWith(expected);
        error.ShouldNotContain("LineNumber");
    }

    [Fact]
    public async Task Windows1252Body_SaysToSendUtf8()
    {
        // "Dry run — 8.0" as Windows-1252: the em dash is the single byte 0x97, which isn't UTF-8.
        var body = Encoding.ASCII.GetBytes("""{"directory":"/tmp/x","title":"Dry run X 8.0"}""");
        body[Array.IndexOf(body, (byte)'X')] = 0x97;

        var (status, error) = await PostAsync("/api/sessions", body);

        status.ShouldBe(HttpStatusCode.BadRequest);
        error.ShouldBe("The body isn't valid UTF-8 (in \"title\"). Send the JSON encoded as UTF-8.");
    }

    [Fact]
    public async Task CharsetFleetCantRead_SaysToSendUtf8()
    {
        var (status, error) = await PostAsync("/api/sessions", Encoding.ASCII.GetBytes("""{"directory":"/tmp/x"}"""),
            "application/json; charset=windows-1252");

        status.ShouldBe(HttpStatusCode.UnsupportedMediaType);
        error.ShouldBe("The body says it's in \"windows-1252\", which Fleet can't read. Send the JSON encoded as UTF-8.");
    }

    [Fact]
    public async Task NotJsonContentType_SaysWhichContentTypeToSend()
    {
        // Invoke-RestMethod without -ContentType sends a string body as a form.
        var (status, error) = await PostAsync("/api/sessions", Encoding.UTF8.GetBytes("""{"directory":"/tmp/x"}"""),
            "application/x-www-form-urlencoded");

        status.ShouldBe(HttpStatusCode.UnsupportedMediaType);
        error.ShouldBe("Send the body as JSON with Content-Type: application/json (this request said \"application/x-www-form-urlencoded\").");
    }

    [Fact]
    public async Task EmptyBody_SaysWhatTheEndpointTakes()
    {
        var (status, error) = await PostAsync("/api/boards", Array.Empty<byte>());

        status.ShouldBe(HttpStatusCode.BadRequest);
        error.ShouldBe("The body is empty. This endpoint takes a JSON object with these fields: name.");
    }

    [Fact]
    public async Task OtherEndpoints_AnswerTheSameWay()
    {
        var (status, error) = await PostAsync("/api/boards", """{"title":"Release"}""");

        status.ShouldBe(HttpStatusCode.BadRequest);
        error.ShouldBe("Unknown field \"title\". Fields this endpoint takes: name.");
    }

    [Fact]
    public async Task Utf8Bom_IsRead()
    {
        // Set-Content -Encoding UTF8 and Out-File in Windows PowerShell start the file with a byte order mark.
        using var content = new ByteArrayContent([.. Utf8Bom, .. Encoding.UTF8.GetBytes("""{"name":"Release"}""")]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        var response = await _client.PostAsync("/api/boards", content);

        response.IsSuccessStatusCode.ShouldBeTrue(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Utf8Bom_WithAnUnknownField_StillSaysWhichField()
    {
        var (status, error) = await PostAsync("/api/sessions", [.. Utf8Bom, .. Encoding.UTF8.GetBytes("""{"prompt":"x"}""")]);

        status.ShouldBe(HttpStatusCode.BadRequest);
        error.ShouldStartWith("Unknown field \"prompt\".");
    }

    [Fact]
    public async Task HandlerErrors_AreUnchanged()
    {
        var (status, error) = await PostAsync("/api/boards", "{}");

        status.ShouldBe(HttpStatusCode.BadRequest);
        error.ShouldBe("Board name is required.");
    }

    private Task<(HttpStatusCode Status, string Error)> PostAsync(string path, string json) =>
        PostAsync(path, Encoding.UTF8.GetBytes(json));

    private async Task<(HttpStatusCode Status, string Error)> PostAsync(string path, byte[] body, string contentType = "application/json")
    {
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);

        var response = await _client.PostAsync(path, content);
        var text = await response.Content.ReadAsStringAsync();

        // The same shape every other API error has, so agents and the client read it the same way.
        text.ShouldNotBeNullOrEmpty($"{(int)response.StatusCode} with an empty body");
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/json");
        using var json = JsonDocument.Parse(text);
        return (response.StatusCode, json.RootElement.GetProperty("error").GetString()!);
    }
}

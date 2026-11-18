using System.Net;
using System.Text;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode2;

/// <summary>What a person reads when OpenCode 2 refuses a request: its message, not its raw JSON body.</summary>
public sealed class OpenCode2ErrorBodyTests
{
    [Fact]
    public async Task A_500_with_a_json_body_shows_its_message_and_ref_not_the_raw_json()
    {
        var api = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            ReasonPhrase = "Internal Server Error",
            Content = new StringContent(
                """{"_tag":"UnknownError","message":"The database is read-only.","ref":"err_1a2b3c4d"}""", Encoding.UTF8, "application/json"),
        });
        using var client = OpenCode2Fixtures.ClientServing("", api);

        var error = await Should.ThrowAsync<HttpRequestException>(() => client.CreateSessionAsync("/work/demo", CancellationToken.None));

        error.Message.ShouldBe("OpenCode 2 couldn't create a session: 500 Internal Server Error. The database is read-only. (ref err_1a2b3c4d)");
        error.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task A_body_that_is_not_json_is_shown_as_it_is()
    {
        var api = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            ReasonPhrase = "Bad Gateway",
            Content = new StringContent("upstream went away", Encoding.UTF8, "text/plain"),
        });
        using var client = OpenCode2Fixtures.ClientServing("", api);

        var error = await Should.ThrowAsync<HttpRequestException>(() => client.CreateSessionAsync("/work/demo", CancellationToken.None));

        error.Message.ShouldBe("OpenCode 2 couldn't create a session: 502 Bad Gateway. upstream went away");
    }
}

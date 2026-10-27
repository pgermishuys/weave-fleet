using System.Net.Http.Json;
using System.Text.Json;

namespace WeaveFleet.Api.Tests.Infrastructure;

public static class HttpResponseAssertions
{
    /// <summary>
    /// The JSON body of a successful response. A failed one fails the test with its status and body, so it says why
    /// (e.g. "Couldn't reach …") instead of a missing property further on.
    /// </summary>
    public static async Task<JsonElement> ReadSuccessAsync(this HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new Shouldly.ShouldAssertException(
                $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.PathAndQuery} answered {(int)response.StatusCode}: {body}");
        }

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}

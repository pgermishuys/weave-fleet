using NuCode.Providers.Auth;

namespace WeaveFleet.Infrastructure.Harnesses.NuCode;

/// <summary>
/// Adapts <see cref="IHttpClientFactory"/> to NuCode's <see cref="INuCodeHttpClient"/> interface.
/// </summary>
internal sealed class HttpClientAdapter(IHttpClientFactory httpClientFactory) : INuCodeHttpClient
{
    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default)
    {
        // Do NOT dispose the HttpClient here — the caller needs to read the response body.
        // HttpClients created by IHttpClientFactory are pooled; disposal just returns the handler to the pool.
        var client = httpClientFactory.CreateClient();
        return await client.SendAsync(request, ct).ConfigureAwait(false);
    }
}

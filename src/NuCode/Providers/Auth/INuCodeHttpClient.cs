namespace NuCode.Providers.Auth;

/// <summary>
/// Minimal HTTP abstraction for NuCode's auth flows.
/// Allows NuCode to make HTTP requests without depending on <c>IHttpClientFactory</c>
/// or any ASP.NET Core infrastructure.
/// The host (e.g. Fleet) provides the implementation.
/// </summary>
public interface INuCodeHttpClient
{
    /// <summary>
    /// Sends an HTTP request and returns the response.
    /// </summary>
    Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default);
}

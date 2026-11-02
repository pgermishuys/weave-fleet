using Microsoft.Extensions.DependencyInjection;

namespace WeaveFleet.Infrastructure.Harnesses;

/// <summary>
/// HTTP clients that only ever talk to a harness server Fleet started on 127.0.0.1. .NET sends those requests to the
/// environment's <c>HTTP_PROXY</c> unless <c>NO_PROXY</c> lists loopback, so behind a proxy every call to the harness
/// would leave the machine and fail. These clients never use a proxy.
/// </summary>
internal static class LoopbackHttpClients
{
    public static IHttpClientBuilder AddLoopbackHttpClient(this IServiceCollection services, string name, Action<HttpClient>? configure = null)
    {
        var builder = configure is null ? services.AddHttpClient(name) : services.AddHttpClient(name, configure);
        return builder.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { UseProxy = false });
    }
}

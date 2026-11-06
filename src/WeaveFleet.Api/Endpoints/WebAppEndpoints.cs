using Microsoft.AspNetCore.StaticFiles;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

/// <summary>Serves the web app (wwwroot): index.html, its hashed assets, the manifest and the service worker.</summary>
public static class WebAppEndpoints
{
    /// <summary>
    /// Hashed assets (e.g. /assets/index-abc123.js) get immutable long-lived cache. Everything else (index.html) gets
    /// no-cache so browsers always fetch the latest entry point.
    /// The web app manifest needs its own type for browsers to offer to install Fleet. The service worker (/sw.js) is
    /// unhashed and must never be cached stale, so it falls under no-cache; it may control the whole origin.
    /// </summary>
    internal static StaticFileOptions StaticFileOptions()
    {
        var staticContentTypes = new FileExtensionContentTypeProvider();
        staticContentTypes.Mappings[".webmanifest"] = "application/manifest+json";
        return new StaticFileOptions
        {
            ContentTypeProvider = staticContentTypes,
            OnPrepareResponse = ctx =>
            {
                var path = ctx.Context.Request.Path.Value ?? string.Empty;
                if (path.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
                }
                else
                {
                    ctx.Context.Response.Headers.CacheControl = "no-cache";
                }

                if (string.Equals(path, "/sw.js", StringComparison.OrdinalIgnoreCase))
                    ctx.Context.Response.Headers["Service-Worker-Allowed"] = "/";
            },
        };
    }

    /// <summary>
    /// Paths only the server answers. None is a page of the web app, so a request under one that no endpoint takes is
    /// a mistake (a mistyped call, a URL missing its id) and gets a 404 rather than index.html.
    /// </summary>
    internal static readonly string[] ServerPathPrefixes = ["/api", "/hubs", "/pages", "/auth", "/openapi"];

    /// <summary>
    /// A 404 with an <see cref="ErrorResponse"/> for a GET or HEAD of a server path nothing answers. Without it the SPA
    /// fallback answered with index.html and a 200, so a client, another machine or an agent read HTML where it
    /// expected JSON. A fallback ranks below every real endpoint, and its literal prefix above the SPA's catch-all.
    /// GET and HEAD only, like the SPA fallback: one that took every method would also take a POST an endpoint turns
    /// away for its method or content type, and answer 404 where routing answers 405 or 415.
    /// </summary>
    public static IEndpointRouteBuilder MapUnknownServerPaths(this IEndpointRouteBuilder app)
    {
        // A RequestDelegate, not a handler: the request delegate generator (on in Release) rewrites
        // MapFallback(pattern, handler) without its pattern, which made it a second SPA fallback.
        RequestDelegate notFound = http =>
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return http.Response.WriteAsJsonAsync(
                new ErrorResponse($"Fleet has nothing at {http.Request.Method} {http.Request.Path}."),
                ApiJsonContext.Default.ErrorResponse);
        };

        foreach (var prefix in ServerPathPrefixes)
        {
            app.MapFallback(prefix + "/{**path}", notFound)
                .WithMetadata(new HttpMethodMetadata([HttpMethods.Get, HttpMethods.Head]))
                .AllowAnonymous();
        }

        return app;
    }

    public static WebApplication UseFleetWebApp(this WebApplication app)
    {
        var options = StaticFileOptions();
        app.UseDefaultFiles(); // Serves index.html for "/"
        app.UseStaticFiles(options);
        app.MapUnknownServerPaths();

        // SPA fallback — any unmatched route serves index.html for client-side routing. It takes the same options:
        // "/" and every deep link (/settings, /sessions/…) end here rather than in UseStaticFiles above, and without
        // no-cache a browser may keep showing the index.html, and so the UI, of the Fleet it had before an update.
        app.MapFallbackToFile("index.html", options)
            .AllowAnonymous();

        return app;
    }
}
#pragma warning restore IL2026

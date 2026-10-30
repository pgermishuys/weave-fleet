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

    public static WebApplication UseFleetWebApp(this WebApplication app)
    {
        var options = StaticFileOptions();
        app.UseDefaultFiles(); // Serves index.html for "/"
        app.UseStaticFiles(options);

        // SPA fallback — any unmatched route serves index.html for client-side routing. It takes the same options:
        // "/" and every deep link (/settings, /sessions/…) end here rather than in UseStaticFiles above, and without
        // no-cache a browser may keep showing the index.html, and so the UI, of the Fleet it had before an update.
        app.MapFallbackToFile("index.html", options)
            .AllowAnonymous();

        return app;
    }
}
#pragma warning restore IL2026

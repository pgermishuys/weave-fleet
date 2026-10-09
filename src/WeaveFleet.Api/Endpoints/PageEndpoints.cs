using Microsoft.AspNetCore.StaticFiles;
using WeaveFleet.Application.Pages;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

/// <summary>
/// Serves the pages agents show (<c>fleet_page_show</c>) at <c>/pages/{pageId}/…</c>, from Fleet's own copy.
/// <para>
/// The page runs sandboxed wherever it's opened, in the canvas or a tab of its own: the CSP below gives it an opaque
/// origin, so its scripts run but can't read Fleet's cookies or call Fleet as the user. The same sandbox means the
/// page's own requests for its CSS and images are cross-site, and Fleet's <c>SameSite=Lax</c> sign-in cookie isn't
/// sent with them. So these routes sit outside sign-in, and the page id, 128 random bits, is what it takes to open
/// a page, as with previews. <c>no-referrer</c> keeps the address out of the requests a page makes to CDNs.
/// </para>
/// <para>Every HTML file gets Fleet's theme as <c>--fleet-*</c> CSS variables (<see cref="PageTheme"/>).</para>
/// </summary>
public static class PageEndpoints
{
    public const string PathPrefix = "/pages";

    /// <summary>Scripts, forms, pop-ups and dialogs work; storage, cookies and Fleet's origin don't.</summary>
    public const string Sandbox = "sandbox allow-scripts allow-forms allow-popups allow-popups-to-escape-sandbox allow-modals allow-downloads";

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public static IEndpointRouteBuilder MapPageEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(PathPrefix)
            .AllowAnonymous()
            .WithTags("Pages")
            .ExcludeFromDescription();

        // Routing matches /pages/{id}/ here too; that one serves the page. Without the slash, the page's relative
        // links would resolve against /pages/, so it gets one.
        group.MapMethods("/{pageId}", ["GET", "HEAD"], (string pageId, HttpContext http, IPageStore pages)
            => http.Request.Path.Value?.EndsWith('/') == true
                ? Serve(pages, pageId, string.Empty, http)
                : PageIds.IsValid(pageId) ? Results.Redirect($"{PathPrefix}/{pageId}/{http.Request.QueryString}") : Results.NotFound())
            .WithName("PageRoot");

        group.MapMethods("/{pageId}/{**path}", ["GET", "HEAD"], (string pageId, string? path, HttpContext http, IPageStore pages)
            => Serve(pages, pageId, path ?? string.Empty, http))
            .WithName("PageFile");

        return app;
    }

    private static IResult Serve(IPageStore pages, string pageId, string path, HttpContext http)
    {
        if (pages.Resolve(pageId, path) is not { } file)
            return Results.NotFound();

        var headers = http.Response.Headers;
        headers.ContentSecurityPolicy = Sandbox;
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers.CacheControl = "no-cache";

        var contentType = ContentTypes.TryGetContentType(file, out var known) ? known : "application/octet-stream";
        // An HTML file gets Fleet's theme at the top of its head (PageTheme), so the page can follow the user's theme.
        return PageTheme.Themes(file)
            ? Results.Bytes(PageTheme.Inject(File.ReadAllBytes(file)), contentType, enableRangeProcessing: true)
            : Results.File(file, contentType, enableRangeProcessing: true);
    }
}
#pragma warning restore IL2026

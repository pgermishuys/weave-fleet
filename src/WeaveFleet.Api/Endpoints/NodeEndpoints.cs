using System.Net;
using WeaveFleet.Api.Auth;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Diagnostics;
using WeaveFleet.Application.Users;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

/// <summary>
/// A Fleet started as a node (<c>fleet node</c>, <see cref="FleetOptions.ServeUi"/> off): the API without the web app.
/// Another Fleet adds it under Settings → Machines. See docs/machines.md.
/// </summary>
public static class NodeEndpoints
{
    public const string DocsUrl = "https://fleet.tryweave.io/docs/machines/";

    /// <summary>
    /// The page a browser gets at <c>/</c>, in Fleet's theme: what this is and how to add it. Built once, with Fleet's
    /// logo inlined, because a node serves no other files.
    /// </summary>
    internal static readonly Lazy<string> Page = new(() =>
    {
        var assembly = typeof(NodeEndpoints).Assembly;
        using var pageStream = assembly.GetManifestResourceStream("node/node-page.html")
            ?? throw new InvalidOperationException("Embedded resource node/node-page.html is missing.");
        using var logoStream = assembly.GetManifestResourceStream("node/weave-logo.png")
            ?? throw new InvalidOperationException("Embedded resource node/weave-logo.png is missing.");
        using var reader = new StreamReader(pageStream);
        using var logo = new MemoryStream();
        logoStream.CopyTo(logo);

        return reader.ReadToEnd()
            .Replace("{{logo}}", "data:image/png;base64," + Convert.ToBase64String(logo.ToArray()), StringComparison.Ordinal)
            .Replace("{{docs}}", DocsUrl, StringComparison.Ordinal)
            .Replace("{{version}}", WebUtility.HtmlEncode("v" + FleetInstrumentation.ServiceVersion.Split('+')[0]), StringComparison.Ordinal);
    });

    /// <summary>
    /// <c>/</c> says what answers here: a page for someone who opens the node's address in a browser, and a short JSON
    /// note for anything else (curl, scripts). <c>/add-machine</c> answers the same, so a pairing link opened in a
    /// browser says to paste it into Fleet instead.
    /// </summary>
    public static IEndpointRouteBuilder MapNodeNote(this IEndpointRouteBuilder app)
    {
        foreach (var path in new[] { "/", "/add-machine" })
            app.MapGet(path, (HttpContext http) =>
            {
                if (!http.Request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase))
                    return Results.Ok(new NodeNoteResponse(
                        "fleet-node",
                        "This is a Fleet machine without the web app. On this machine run fleet node pair, then paste the link it prints into Add a machine in another Fleet.",
                        DocsUrl));

                // The page's own inline style, its inlined logo, and nothing else; no one may frame it.
                var headers = http.Response.Headers;
                headers.ContentSecurityPolicy = "default-src 'none'; img-src data:; style-src 'unsafe-inline'; frame-ancestors 'none'";
                headers.XContentTypeOptions = "nosniff";
                headers.CacheControl = "no-cache";
                return Results.Content(Page.Value, "text/html; charset=utf-8");
            })
            .AllowAnonymous()
            .ExcludeFromDescription();

        return app;
    }

    /// <summary>What a node prints at startup: where it listens, and what another Fleet needs to add it.</summary>
    internal static IReadOnlyList<string> StartupLines(
        FleetOptions options,
        LoopbackAuthPolicy policy,
        ILocalTokenAuthService tokens,
        string tokenFilePath)
    {
        var lines = new List<string>
        {
            $"  Fleet node on {options.Host}:{options.Port}, a machine without the web app.",
            "",
        };

        if (policy.IsRemoteReachable)
        {
            lines.Add("  To add it to another Fleet, run this on this machine:");
            lines.Add("");
            lines.Add("    fleet node pair");
            lines.Add("");
            lines.Add("  Then paste the link it prints into Settings → Machines → Add a machine.");
        }
        else
        {
            lines.Add($"  It listens on {options.Host} only, so other machines can't reach it directly yet. Run");
            lines.Add("  fleet node up and pick Tailscale or Local network, then run fleet node pair.");
        }

        lines.Add("");
        lines.Add(tokens.Source switch
        {
            LocalTokenSource.Environment => "  Scripts can use the master token in WEAVE_FLEET_AUTH_TOKEN.",
            LocalTokenSource.Saved => $"  Scripts can use the master token in {tokenFilePath}.",
            _ => "  Scripts can use the master token, which lasts until Fleet stops.",
        });

        return lines;
    }
}

/// <summary>What a node answers at <c>/</c>.</summary>
/// <param name="Kind">Always <c>fleet-node</c>.</param>
/// <param name="Message">A sentence for whoever opened the address.</param>
/// <param name="Docs">Where running and adding a node is explained.</param>
public sealed record NodeNoteResponse(string Kind, string Message, string Docs);
#pragma warning restore IL2026

using System.Net;
using WeaveFleet.Api.Auth;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Diagnostics;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

/// <summary>
/// A Fleet started as a node (<c>fleet node</c>, <see cref="FleetOptions.ServeUi"/> off): the API without the web app.
/// Another Fleet adds it under Settings → Machines. See docs/machines.md.
/// </summary>
public static class NodeEndpoints
{
    public const string DocsUrl = "https://github.com/pgermishuys/weave-fleet/blob/main/docs/machines.md#run-a-node-without-the-web-ui";

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
    /// note for anything else (curl, scripts).
    /// </summary>
    public static IEndpointRouteBuilder MapNodeNote(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", (HttpContext http) =>
            {
                if (!http.Request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase))
                    return Results.Ok(new NodeNoteResponse(
                        "fleet-node",
                        "This is a Fleet node. It has no web app. Add it to another Fleet under Settings → Machines, with this address and its access token.",
                        DocsUrl));

                // The page's own inline style and script, its inlined logo, and nothing else; no one may frame it.
                var headers = http.Response.Headers;
                headers.ContentSecurityPolicy = "default-src 'none'; img-src data:; style-src 'unsafe-inline'; script-src 'unsafe-inline'; frame-ancestors 'none'";
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
            $"  Fleet node on {options.Host}:{options.Port}, without the web app. Every request needs the access token.",
            "",
            "  To add it, open another Fleet: Settings → Machines → Add a machine.",
        };

        var urls = policy.IsRemoteReachable
            ? MachineEndpoints.ReachableAddresses(options).Select(address => address.Url).ToList()
            : [$"http://localhost:{options.Port}"];
        for (var i = 0; i < urls.Count; i++)
            lines.Add($"    {(i == 0 ? "URL:" : ""),-6}  {urls[i]}");
        lines.Add($"    Token:  {tokens.Token}");
        lines.Add("");

        if (!policy.IsRemoteReachable)
        {
            lines.Add($"  It listens on {options.Host} only, so other machines can't reach it yet. Start it with");
            lines.Add("  --host 0.0.0.0, or put tailscale serve in front of it (see docs/machines.md).");
            lines.Add("");
        }

        lines.Add(tokens.Source switch
        {
            LocalTokenSource.Environment => "  WEAVE_FLEET_AUTH_TOKEN sets the token.",
            LocalTokenSource.Saved => $"  Fleet keeps the token in {tokenFilePath}, so it stays the same after a restart.",
            _ => "  This token lasts until Fleet stops.",
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

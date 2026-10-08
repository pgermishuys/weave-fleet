using WeaveFleet.Api.Auth;
using WeaveFleet.Application.Configuration;
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

    /// <summary><c>/</c> says what answers here, for someone who opens the node's address in a browser.</summary>
    public static IEndpointRouteBuilder MapNodeNote(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", () => Results.Ok(new NodeNoteResponse(
            "fleet-node",
            "This is a Fleet node. It has no web app. Add it to another Fleet under Settings → Machines, with this address and its access token.",
            DocsUrl)))
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

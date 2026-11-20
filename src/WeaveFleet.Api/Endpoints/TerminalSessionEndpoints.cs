using WeaveFleet.Api.Auth;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.TerminalSessions;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

/// <summary>
/// <c>POST /api/machine/terminal-sessions</c>: what <c>fleet mcp</c>, the MCP server Claude Code starts with a session in a
/// terminal, tells the Fleet on its machine: the session started, or ended. Owner only: it is signed with the machine token
/// that sits next to the database, as <c>fleet node pair</c> is.
/// </summary>
public static class TerminalSessionEndpoints
{
    public static IEndpointRouteBuilder MapTerminalSessionEndpoints(this IEndpointRouteBuilder app, FleetOptions fleetOptions)
    {
        // The machine token only exists in local mode, where whoever holds it owns the machine.
        if (fleetOptions.Auth.Enabled || !fleetOptions.Auth.TokenAuthEnabled)
            return app;

        app.MapGroup("/api/machine").WithTags("Terminal sessions")
            .MapPost("/terminal-sessions", async (TerminalSessionReport report, ITerminalSessions terminalSessions, CancellationToken ct) =>
                Results.Ok(await terminalSessions.ReportAsync(report, ct)))
            .RequireAuthorization(FleetClaims.MachineOwnerPolicy)
            .Produces<TerminalSessionAnswer>(200)
            .WithName("ReportTerminalSession");

        return app;
    }
}

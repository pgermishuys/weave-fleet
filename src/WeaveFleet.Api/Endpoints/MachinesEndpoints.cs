using WeaveFleet.Api.Auth;
using WeaveFleet.Api.Contracts;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Machines;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

/// <summary>
/// The other machines this Fleet knows, kept on the server (it used to be each browser's own list). Anyone signed in
/// can read the list; only the owner sees tokens and changes it. See docs/machines.md.
/// </summary>
public static class MachinesEndpoints
{
    public static IEndpointRouteBuilder MapMachinesEndpoints(this IEndpointRouteBuilder app, FleetOptions fleetOptions)
    {
        if (fleetOptions.Auth.Enabled || !fleetOptions.Auth.TokenAuthEnabled)
            return app;

        var group = app.MapGroup("/api/machines").WithTags("Machines");

        group.MapGet("", async (HttpContext http, RemoteMachineService machines) =>
        {
            var owner = FleetClaims.IsOwner(http.User);
            var list = await machines.ListAsync();
            return Results.Ok(new MachineListResponse(list.Select(m => ToResponse(m, owner ? machines.TokenOf(m) : null)).ToList()));
        })
        .Produces<MachineListResponse>(200)
        .WithName("ListMachines");

        group.MapPost("", async (AddMachineRequest request, RemoteMachineService machines, CancellationToken cancellationToken) =>
        {
            var (machine, error) = await machines.AddAsync(request.BaseUrl ?? "", request.Token ?? "", cancellationToken);
            return machine is null
                ? Results.BadRequest(new ErrorResponse(error!))
                : Results.Ok(ToResponse(machine, machines.TokenOf(machine)));
        })
        .RequireAuthorization(FleetClaims.MachineOwnerPolicy)
        .Produces<MachineEntryResponse>(200)
        .WithName("AddMachine");

        group.MapPut("/{id}", async (string id, UpdateRemoteMachineRequest request, RemoteMachineService machines, CancellationToken cancellationToken) =>
        {
            var (machine, error) = await machines.UpdateAsync(id, request.BaseUrl, request.Token, request.Name, cancellationToken);
            if (machine is null)
                return error == "No such machine." ? Results.NotFound(new ErrorResponse(error)) : Results.BadRequest(new ErrorResponse(error!));
            return Results.Ok(ToResponse(machine, machines.TokenOf(machine)));
        })
        .RequireAuthorization(FleetClaims.MachineOwnerPolicy)
        .Produces<MachineEntryResponse>(200)
        .WithName("UpdateRemoteMachine");

        group.MapDelete("/{id}", async (string id, RemoteMachineService machines) =>
            await machines.DeleteAsync(id) ? Results.NoContent() : Results.NotFound(new ErrorResponse("No such machine.")))
        .RequireAuthorization(FleetClaims.MachineOwnerPolicy)
        .WithName("RemoveRemoteMachine");

        group.MapPost("/import", async (ImportMachinesRequest request, RemoteMachineService machines) =>
        {
            var imported = (request.Machines ?? [])
                .Where(m => m.Id is not null && m.BaseUrl is not null && m.Token is not null)
                .Select(m => new ImportedMachine(m.Id!, m.Name, m.BaseUrl!, m.Token!, m.Os, m.AddedAt));
            var list = await machines.ImportAsync(imported);
            return Results.Ok(new MachineListResponse(list.Select(m => ToResponse(m, machines.TokenOf(m))).ToList()));
        })
        .RequireAuthorization(FleetClaims.MachineOwnerPolicy)
        .Produces<MachineListResponse>(200)
        .WithName("ImportMachines");

        return app;
    }

    private static MachineEntryResponse ToResponse(RemoteMachine machine, string? token) =>
        new(machine.Id, machine.Name, machine.BaseUrl, machine.Os, machine.Status, machine.AddedAt, machine.LastSeenAt, token);
}
#pragma warning restore IL2026

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
        group.AddEndpointFilter<AgentRequests.WritesAreTheUsers>();

        group.MapGet("", async (HttpContext http, RemoteMachineService machines, ISshTunnels tunnels) =>
        {
            var owner = FleetClaims.IsOwner(http.User);
            var list = await machines.ListAsync();
            return Results.Ok(new MachineListResponse(list.Select(m => ToResponse(m, owner ? machines.TryTokenOf(m) : null, tunnels)).ToList()));
        })
        .Produces<MachineListResponse>(200)
        .WithName("ListMachines");

        group.MapPost("", async (AddMachineRequest request, RemoteMachineService machines, ISshTunnels tunnels, CancellationToken cancellationToken) =>
        {
            var (machine, error) = await machines.AddAsync(request.BaseUrl ?? "", request.Token ?? "", cancellationToken);
            return machine is null
                ? Results.BadRequest(new ErrorResponse(error!))
                : Results.Ok(ToResponse(machine, machines.TokenOf(machine), tunnels));
        })
        .RequireAuthorization(FleetClaims.MachineOwnerPolicy)
        .Produces<MachineEntryResponse>(200)
        .WithName("AddMachine");

        group.MapPost("/pair", async (PairMachineRequest request, RemoteMachineService machines, ISshTunnels tunnels, CancellationToken cancellationToken) =>
        {
            var (machine, error) = await machines.AddByCodeAsync(request.BaseUrl ?? "", request.Code ?? "", cancellationToken);
            return machine is null
                ? Results.BadRequest(new ErrorResponse(error!))
                : Results.Ok(ToResponse(machine, machines.TokenOf(machine), tunnels));
        })
        .RequireAuthorization(FleetClaims.MachineOwnerPolicy)
        .Produces<MachineEntryResponse>(200)
        .WithName("PairMachine");

        // Hosts from ~/.ssh/config on this machine, for Add a machine → Over SSH.
        group.MapGet("/ssh-hosts", () =>
            Results.Ok(new SshHostListResponse(SshConfigHosts.ReadDefault()
                .Select(h => new SshHostResponse(h.Alias, h.Alias, h.HostName, h.User, h.Port)).ToList())))
        .RequireAuthorization(FleetClaims.MachineOwnerPolicy)
        .Produces<SshHostListResponse>(200)
        .WithName("ListSshHosts");

        group.MapPost("/ssh", async (AddSshMachineRequest request, SshMachineService ssh, RemoteMachineService machines, ISshTunnels tunnels, CancellationToken cancellationToken) =>
        {
            var (machine, error) = await ssh.AddAsync(request.Destination ?? "", cancellationToken);
            return machine is null
                ? Results.BadRequest(new ErrorResponse(error!))
                : Results.Ok(ToResponse(machine, machines.TokenOf(machine), tunnels));
        })
        .RequireAuthorization(FleetClaims.MachineOwnerPolicy)
        .Produces<MachineEntryResponse>(200)
        .WithName("AddMachineOverSsh");

        group.MapPut("/{id}", async (string id, UpdateRemoteMachineRequest request, RemoteMachineService machines, ISshTunnels tunnels, CancellationToken cancellationToken) =>
        {
            var (machine, error) = await machines.UpdateAsync(id, request.BaseUrl, request.Token, request.Name, cancellationToken, request.AgentsAllowed);
            if (machine is null)
                return error == "No such machine." ? Results.NotFound(new ErrorResponse(error)) : Results.BadRequest(new ErrorResponse(error!));
            return Results.Ok(ToResponse(machine, machines.TokenOf(machine), tunnels));
        })
        .RequireAuthorization(FleetClaims.MachineOwnerPolicy)
        .Produces<MachineEntryResponse>(200)
        .WithName("UpdateRemoteMachine");

        // Phones' tokens on the machine go first, while there's still a token to remove them with.
        group.MapDelete("/{id}", async (string id, RemoteMachineService machines, DeviceGrantService grants, CancellationToken cancellationToken) =>
        {
            await grants.RemoveGrantsOnAsync(id, cancellationToken);
            return await machines.DeleteAsync(id) ? Results.NoContent() : Results.NotFound(new ErrorResponse("No such machine."));
        })
        .RequireAuthorization(FleetClaims.MachineOwnerPolicy)
        .WithName("RemoveRemoteMachine");

        // A paired phone asks home for its own token on another machine in the list.
        group.MapPost("/{id}/device-grant", async (
            HttpContext http,
            string id,
            WeaveFleet.Application.Devices.DeviceTokenService devices,
            DeviceGrantService grants,
            CancellationToken cancellationToken) =>
        {
            if (FleetClaims.DeviceIdOf(http.User) is not { } deviceId)
                return Results.BadRequest(new ErrorResponse("Only a paired device asks for a device grant; a computer uses the machine token."));
            var device = await devices.ValidateDeviceAsync(deviceId);
            if (device is null)
                return Results.Unauthorized();
            var platform = (await devices.ListAsync()).FirstOrDefault(d => d.Id == deviceId)?.Platform;

            var result = await grants.GrantAsync(deviceId, device.Name, platform, id, cancellationToken);
            return result.Grant is { } grant
                ? Results.Ok(new DeviceGrantResponse(grant.MachineId, grant.BaseUrl, grant.Token))
                : result.Failure == GrantFailure.NoSuchMachine
                    ? Results.NotFound(new ErrorResponse(result.Error!))
                    : Results.Json(new ErrorResponse(result.Error!), ApiJsonContext.Default.ErrorResponse, statusCode: StatusCodes.Status502BadGateway);
        })
        .Produces<DeviceGrantResponse>(200)
        .WithName("CreateDeviceGrant");

        group.MapPost("/import", async (ImportMachinesRequest request, RemoteMachineService machines, ISshTunnels tunnels) =>
        {
            var imported = (request.Machines ?? [])
                .Where(m => m.Id is not null && m.BaseUrl is not null && m.Token is not null)
                .Select(m => new ImportedMachine(m.Id!, m.Name, m.BaseUrl!, m.Token!, m.Os, m.AddedAt));
            var list = await machines.ImportAsync(imported);
            return Results.Ok(new MachineListResponse(list.Select(m => ToResponse(m, machines.TryTokenOf(m), tunnels)).ToList()));
        })
        .RequireAuthorization(FleetClaims.MachineOwnerPolicy)
        .Produces<MachineListResponse>(200)
        .WithName("ImportMachines");

        return app;
    }

    private static MachineEntryResponse ToResponse(RemoteMachine machine, string? token, ISshTunnels tunnels) =>
        new(machine.Id, machine.Name, machine.BaseUrl, machine.Os, machine.Status, machine.AddedAt, machine.LastSeenAt, token, machine.AgentsAllowed,
            machine.SshDestination, machine.SshDestination is null ? null : tunnels.IsUp(machine.Id));
}
#pragma warning restore IL2026

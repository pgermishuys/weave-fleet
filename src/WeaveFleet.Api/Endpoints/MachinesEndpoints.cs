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
                ? Results.BadRequest(ToError(error!))
                : Results.Ok(ToResponse(machine, machines.TokenOf(machine), tunnels));
        })
        .RequireAuthorization(FleetClaims.MachineOwnerPolicy)
        .Produces<MachineEntryResponse>(200)
        .WithName("AddMachine");

        // What Add a machine shows once a pairing link is pasted: the machine behind it, before the code is used.
        group.MapPost("/pair/preview", async (PairMachineRequest request, RemoteMachineService machines, CancellationToken cancellationToken) =>
        {
            var (found, error) = await machines.PreviewByCodeAsync(request.BaseUrl ?? "", request.Code ?? "", cancellationToken);
            return found is null
                ? Results.BadRequest(ToError(error!))
                : Results.Ok(new PairMachinePreviewResponse(found.MachineId, found.Name, found.Os, found.Version, found.BaseUrl));
        })
        .RequireAuthorization(FleetClaims.MachineOwnerPolicy)
        .Produces<PairMachinePreviewResponse>(200)
        .WithName("PreviewPairMachine");

        group.MapPost("/pair", async (PairMachineRequest request, RemoteMachineService machines, ISshTunnels tunnels, CancellationToken cancellationToken) =>
        {
            var (machine, error) = await machines.AddByCodeAsync(request.BaseUrl ?? "", request.Code ?? "", cancellationToken);
            return machine is null
                ? Results.BadRequest(ToError(error!))
                : Results.Ok(ToResponse(machine, machines.TokenOf(machine), tunnels));
        })
        .RequireAuthorization(FleetClaims.MachineOwnerPolicy)
        .Produces<MachineEntryResponse>(200)
        .WithName("PairMachine");

        // The machines Add a machine → Set it up from here lists: ~/.ssh/config hosts and online Tailscale peers.
        group.MapGet("/candidates", async (RemoteMachineService machines, MachineCandidates candidates, CancellationToken cancellationToken) =>
        {
            var added = (await machines.ListAsync()).Select(m => m.SshDestination).OfType<string>().ToHashSet(StringComparer.Ordinal);
            var list = await candidates.ListAsync(added, cancellationToken);
            return Results.Ok(new MachineCandidateListResponse(
                list.Select(c => new MachineCandidateResponse(c.Name, c.Destination, c.Source, c.Os)).ToList(), SshMachineService.Commands));
        })
        .RequireAuthorization(FleetClaims.MachineOwnerPolicy)
        .Produces<MachineCandidateListResponse>(200)
        .WithName("ListMachineCandidates");

        // Looks at one listed machine over SSH. Changes nothing there.
        group.MapPost("/candidates/probe", async (AddSshMachineRequest request, SshMachineService ssh, CancellationToken cancellationToken) =>
        {
            var probe = await ssh.ProbeAsync(request.Destination ?? "", cancellationToken);
            return Results.Ok(new SshProbeResponse(probe.State, probe.Os, probe.Version, probe.Problem?.Message, probe.Problem?.Diagnosis));
        })
        .RequireAuthorization(FleetClaims.MachineOwnerPolicy)
        .Produces<SshProbeResponse>(200)
        .WithName("ProbeMachineCandidate");

        // Sets the machine up over SSH in the background; the steps are read with GET /setup/{id}. Only called after the user clicked.
        group.MapPost("/setup", (AddSshMachineRequest request, SshSetupJobs jobs, RemoteMachineService machines, ISshTunnels tunnels) =>
            Results.Ok(ToResponse(jobs.Start(request.Destination ?? ""), machines, tunnels)))
        .RequireAuthorization(FleetClaims.MachineOwnerPolicy)
        .Produces<SetupJobResponse>(200)
        .WithName("StartMachineSetup");

        group.MapGet("/setup/{id}", (string id, SshSetupJobs jobs, RemoteMachineService machines, ISshTunnels tunnels) =>
            jobs.Get(id) is { } job ? Results.Ok(ToResponse(job, machines, tunnels)) : Results.NotFound(new ErrorResponse("No such set-up.")))
        .RequireAuthorization(FleetClaims.MachineOwnerPolicy)
        .Produces<SetupJobResponse>(200)
        .WithName("GetMachineSetup");

        group.MapPut("/{id}", async (string id, UpdateRemoteMachineRequest request, RemoteMachineService machines, ISshTunnels tunnels, CancellationToken cancellationToken) =>
        {
            var (machine, error) = await machines.UpdateAsync(id, request.BaseUrl, request.Token, request.Name, cancellationToken, request.AgentsAllowed);
            if (machine is null)
                return error!.Message == "No such machine." ? Results.NotFound(new ErrorResponse(error.Message)) : Results.BadRequest(ToError(error));
            return Results.Ok(ToResponse(machine, machines.TokenOf(machine), tunnels));
        })
        .RequireAuthorization(FleetClaims.MachineOwnerPolicy)
        .Produces<MachineEntryResponse>(200)
        .WithName("UpdateRemoteMachine");

        // Asks a saved machine who it is, so its card can say which step broke. Writes nothing.
        group.MapPost("/{id}/check", async (string id, RemoteMachineService machines, CancellationToken cancellationToken) =>
        {
            var (found, problem) = await machines.CheckAsync(id, cancellationToken);
            return !found
                ? Results.NotFound(new ErrorResponse("No such machine."))
                : Results.Ok(new MachineCheckResponse(problem is null, problem?.Message, problem?.Diagnosis));
        })
        .RequireAuthorization(FleetClaims.MachineOwnerPolicy)
        .Produces<MachineCheckResponse>(200)
        .WithName("CheckRemoteMachine");

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

    private static MachineErrorResponse ToError(MachineProblem problem) => new(problem.Message, problem.Diagnosis);

    private static SetupJobResponse ToResponse(SshSetupJob job, RemoteMachineService machines, ISshTunnels tunnels) =>
        new(job.Id, job.Destination, job.Status, job.Progress.Steps,
            job.Result?.Machine is { } machine ? ToResponse(machine, machines.TokenOf(machine), tunnels) : null, job.Result?.Error, job.Result?.Diagnosis);

    private static MachineEntryResponse ToResponse(RemoteMachine machine, string? token, ISshTunnels tunnels) =>
        new(machine.Id, machine.Name, machine.BaseUrl, machine.Os, machine.Status, machine.AddedAt, machine.LastSeenAt, token, machine.AgentsAllowed,
            machine.SshDestination, machine.SshDestination is null ? null : tunnels.IsUp(machine.Id));
}
#pragma warning restore IL2026

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Devices;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Machines;

/// <summary>
/// Gets a phone paired with this machine (its home) its own device token on another machine in home's list, so the
/// phone talks to that machine directly and never sees its machine token. Home asks the other machine with the
/// machine token it keeps (<c>POST /api/machine/devices</c>) and remembers only the other machine's id for the device,
/// never the token: that goes to the phone. Removing the phone here removes it there too; if the other machine is
/// away, the removal waits and is retried when it answers again. Removing the other machine from the list removes the
/// phones' tokens on it first, as far as it answers.
/// </summary>
public sealed partial class DeviceGrantService(
    IRemoteMachineRepository repository,
    RemoteMachineService machines,
    DeviceTokenService devices,
    IHttpClientFactory httpClients,
    MachineIdentityStore identity,
    TimeProvider time,
    ILogger<DeviceGrantService> logger)
{
    /// <summary>
    /// Makes the phone a token on <paramref name="machineId"/>. A grant made before is replaced: the phone only asks
    /// when it has lost its token or the machine stopped taking it.
    /// </summary>
    public async Task<GrantResult> GrantAsync(string deviceId, string deviceName, string? platform, string machineId, CancellationToken cancellationToken)
    {
        var machine = await repository.GetAsync(machineId);
        if (machine is null)
            return GrantResult.Fail(GrantFailure.NoSuchMachine, "That machine isn't in this machine's list.");

        var self = identity.Get();
        var homeName = self.Name ?? Environment.MachineName;
        if (machines.TryTokenOf(machine) is not { } machineToken)
            return GrantResult.Fail(GrantFailure.Refused, $"{homeName} can't read its token for {machine.Name}. Enter it again in Settings › Machines on the computer.");

        // The old token goes before a new one is made: the grant row only remembers one, and a token this machine
        // forgets about could never be removed when the phone is.
        var previous = await repository.GetGrantAsync(deviceId, machineId);
        if (previous is not null)
        {
            if (!await TryRemoveRemoteAsync(machine, previous.RemoteDeviceId, cancellationToken))
                return GrantResult.Fail(GrantFailure.Unreachable, $"Can't reach {machine.Name} from {homeName} right now.");
            await repository.DeleteGrantAsync(deviceId, machineId);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{machine.BaseUrl}/api/machine/devices")
        {
            Content = JsonContent.Create(
                new MintRequest(Clip($"{deviceName} via {homeName}", 60), platform ?? "other", self.Id),
                MachinesJsonContext.Default.MintRequest),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", machineToken);

        HttpResponseMessage response;
        try
        {
            response = await httpClients.CreateClient(RemoteMachineService.HttpClientName).SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return GrantResult.Fail(GrantFailure.Unreachable, $"Can't reach {machine.Name} from {homeName} right now.");
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return GrantResult.Fail(GrantFailure.Refused, $"{machine.Name} turned {homeName}'s token away. Update it in Settings › Machines on the computer.");
            if (response.StatusCode == HttpStatusCode.NotFound)
                return GrantResult.Fail(GrantFailure.Refused, $"{machine.Name} runs a Fleet too old for phones. Update it.");
            if (!response.IsSuccessStatusCode)
                return GrantResult.Fail(GrantFailure.Unreachable, $"{machine.Name} answered {(int)response.StatusCode}.");

            MintResponse? minted;
            try
            {
                minted = await response.Content.ReadFromJsonAsync(MachinesJsonContext.Default.MintResponse, cancellationToken);
            }
            catch (JsonException)
            {
                minted = null;
            }

            if (minted is null || string.IsNullOrEmpty(minted.Token) || string.IsNullOrEmpty(minted.DeviceId))
                return GrantResult.Fail(GrantFailure.Unreachable, $"{machine.Name} sent something that isn't a token.");

            await repository.SaveGrantAsync(new DeviceGrant
            {
                DeviceId = deviceId,
                MachineId = machineId,
                RemoteDeviceId = minted.DeviceId,
                CreatedAt = time.GetUtcNow(),
            });

            // The phone may have been removed while the other machine was minting. Its removal either already listed
            // this grant or happened before the check below, which then undoes the grant itself.
            if (await devices.ValidateDeviceAsync(deviceId) is null)
            {
                await RevokeAsync(machine, new DeviceGrant { DeviceId = deviceId, MachineId = machineId, RemoteDeviceId = minted.DeviceId }, cancellationToken);
                return GrantResult.Fail(GrantFailure.Refused, "This phone was removed.");
            }

            return new GrantResult(new Grant(machine.Id, machine.BaseUrl, minted.Token), null, null);
        }
    }

    /// <summary>Removes the device's grants everywhere. Machines that don't answer get it when they next do.</summary>
    public async Task RevokeAllAsync(string deviceId, CancellationToken cancellationToken)
    {
        foreach (var grant in await repository.ListGrantsForDeviceAsync(deviceId))
            await RevokeAsync(await repository.GetAsync(grant.MachineId), grant, cancellationToken);
    }

    /// <summary>
    /// Removes every phone's token on a machine that's about to leave the list. Returns how many it couldn't: with the
    /// machine gone there's no token left to retry with, so those stay until removed on the machine itself.
    /// </summary>
    public async Task<int> RemoveGrantsOnAsync(string machineId, CancellationToken cancellationToken)
    {
        var machine = await repository.GetAsync(machineId);
        if (machine is null)
            return 0;

        var left = 0;
        foreach (var grant in await repository.ListGrantsForMachineAsync(machineId))
        {
            if (await TryRemoveRemoteAsync(machine, grant.RemoteDeviceId, cancellationToken))
                await repository.DeleteGrantAsync(grant.DeviceId, grant.MachineId);
            else
                left++;
        }

        if (left > 0)
            LogGrantsLeft(logger, left, machine.Name);
        return left;
    }

    private async Task RevokeAsync(RemoteMachine? machine, DeviceGrant grant, CancellationToken cancellationToken)
    {
        await repository.MarkGrantRevokedAsync(grant.DeviceId, grant.MachineId, time.GetUtcNow());
        if (machine is null || await TryRemoveRemoteAsync(machine, grant.RemoteDeviceId, cancellationToken))
            await repository.DeleteGrantAsync(grant.DeviceId, grant.MachineId);
    }

    /// <summary>Tries again the removals that waited for <paramref name="machineId"/>.</summary>
    public async Task RetryRevocationsAsync(string machineId, CancellationToken cancellationToken)
    {
        var machine = await repository.GetAsync(machineId);
        if (machine is null)
            return;

        foreach (var grant in (await repository.ListRevokedGrantsAsync()).Where(g => g.MachineId == machineId))
        {
            if (await TryRemoveRemoteAsync(machine, grant.RemoteDeviceId, cancellationToken))
                await repository.DeleteGrantAsync(grant.DeviceId, grant.MachineId);
        }
    }

    /// <summary>True when the other machine no longer has the device (removed now, or already gone).</summary>
    private async Task<bool> TryRemoveRemoteAsync(RemoteMachine machine, string remoteDeviceId, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, $"{machine.BaseUrl}/api/machine/devices/{Uri.EscapeDataString(remoteDeviceId)}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", machines.TokenOf(machine));
            using var response = await httpClients.CreateClient(RemoteMachineService.HttpClientName).SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Security.Cryptography.CryptographicException)
        {
            LogRemoveWaits(logger, machine.Name);
            return false;
        }
    }

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max].TrimEnd();

    [LoggerMessage(Level = LogLevel.Information, Message = "Couldn't remove a phone on {Machine} yet; trying again when it answers.")]
    private static partial void LogRemoveWaits(ILogger logger, string machine);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Count} phone token(s) on {Machine} couldn't be removed before it left the list. Remove them in its Settings › Devices.")]
    private static partial void LogGrantsLeft(ILogger logger, int count, string machine);
}

/// <summary>A token the phone can use on another machine.</summary>
public sealed record Grant(string MachineId, string BaseUrl, string Token);

public enum GrantFailure
{
    NoSuchMachine,
    Unreachable,
    Refused,
}

public sealed record GrantResult(Grant? Grant, GrantFailure? Failure, string? Error)
{
    public static GrantResult Fail(GrantFailure failure, string error) => new(null, failure, error);
}

internal sealed record MintRequest(string Name, string Platform, string PairedVia);

internal sealed record MintResponse(string DeviceId, string Token);

[System.Text.Json.Serialization.JsonSerializable(typeof(MintRequest))]
[System.Text.Json.Serialization.JsonSerializable(typeof(MintResponse))]
[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class MachinesJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}

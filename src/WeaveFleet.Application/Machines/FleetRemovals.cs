using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Devices;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Machines;

/// <summary>What a machine sends another about removals: who it is, and the removals it keeps.</summary>
public sealed record RemovalBatch(string? From, IReadOnlyList<FleetRemoval> Removals);

/// <summary>The removals a machine keeps (<c>GET /api/machine/removals</c>).</summary>
public sealed record RemovalList(IReadOnlyList<FleetRemoval> Removals);

/// <summary>
/// Your Fleet (Labs): forgetting a machine, or removing a phone or browser, on one machine reaches every machine.
/// <list type="bullet">
/// <item>The machine the user did it on keeps a <b>removal</b> (what, and when), applies it here, and tells every other
/// machine in its list. Each of those keeps the removal, applies it and tells its own list, so it spreads even where
/// the first machine can't reach.</item>
/// <item>A <b>machine</b> removal: each machine drops it from its list, revokes the keys it holds for it (the phones'
/// and screens' keys on it) and the keys it made for it (on itself, and on the machines that made one for it at its
/// request). The forgotten machine is told too, when it can be reached: it drops the machine that forgot it and
/// revokes the key that machine used.</item>
/// <item>A <b>grantee</b> removal (a phone or a browser): each machine revokes the keys made for it, whichever machine
/// is its home, and refuses to make it another (<see cref="DeviceGrantService"/> answers "removed").</item>
/// <item>The removals are kept, and compared whenever machines compare lists (<see cref="FleetRoster"/>), so a machine
/// that was off applies what it missed when it's back, and a stale list can't introduce a machine again.</item>
/// <item>Adding a forgotten machine back on purpose (by its pairing code or token) clears the removal on the machine it
/// is added on. A removal is only ever applied to a machine added <i>before</i> it, so a machine added again is not
/// forgotten by a stale copy of the old removal. The machine that added it then introduces it to the rest, with the
/// time it was added, which clears the removal on each machine that holds one.</item>
/// </list>
/// </summary>
public sealed partial class FleetRemovals(
    IRemoteMachineRepository repository,
    RemoteMachineService machines,
    DeviceTokenService devices,
    DeviceGrantService grants,
    IHttpClientFactory httpClients,
    MachineIdentityStore identity,
    IServiceScopeFactory scopes,
    TimeProvider time,
    ILogger<FleetRemovals> logger)
{
    private const int MaxIdLength = 128;
    private static readonly TimeSpan TellFor = TimeSpan.FromSeconds(10);

    public Task<FleetRemoval?> GetAsync(string id) => repository.GetRemovalAsync(id);

    public Task<IReadOnlyList<FleetRemoval>> ListAsync() => repository.ListRemovalsAsync();

    public async Task<bool> IsEnabledAsync()
    {
        using var scope = scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<YourFleetFeature>().IsEnabledAsync();
    }

    /// <summary>
    /// The user forgets a machine on this one: it is gone from every machine. False when it isn't in the list. A machine
    /// reached over SSH is only this machine's, so it's removed here and nowhere else.
    /// </summary>
    public async Task<bool> ForgetMachineAsync(string machineId, CancellationToken cancellationToken)
    {
        var machine = await repository.GetAsync(machineId);
        if (machine is null)
            return false;
        if (!FleetRoster.IsPairable(machine))
        {
            await grants.RemoveGrantsOnAsync(machineId, cancellationToken);
            return await machines.DeleteAsync(machineId);
        }

        var removal = new FleetRemoval(machineId, FleetRemovalKinds.Machine, time.GetUtcNow(), identity.Get().Id);
        await repository.SaveRemovalAsync(removal);
        await ForgetLocallyAsync(machine, removal, exceptPeerId: null, cancellationToken);
        return true;
    }

    /// <summary>
    /// The user removes a phone or a browser on this machine: the keys made for it on every machine stop working, and
    /// no machine makes it another. <paramref name="granteeId"/> is the phone's device id, or <c>screen:{id}</c>.
    /// </summary>
    public async Task RemoveGranteeAsync(string granteeId, CancellationToken cancellationToken)
    {
        var removal = new FleetRemoval(granteeId, FleetRemovalKinds.Grantee, time.GetUtcNow(), identity.Get().Id);
        await repository.SaveRemovalAsync(removal);
        await RevokeGranteeLocallyAsync(granteeId, cancellationToken);
        await TellAllAsync([removal], exceptPeerId: null, cancellationToken);
    }

    /// <summary>
    /// Takes in removals another machine sent (or kept). Applies those it didn't know and passes them on. Returns how
    /// many were new. <paramref name="from"/> is the machine that sent them, <paramref name="callerDeviceId"/> the key
    /// it used here.
    /// </summary>
    public async Task<int> ApplyAsync(IReadOnlyList<FleetRemoval> removals, string? from, string? callerDeviceId, CancellationToken cancellationToken)
    {
        var self = identity.Get().Id;
        var applied = 0;
        foreach (var received in removals)
        {
            if (string.IsNullOrWhiteSpace(received.Id) || received.Id.Length > MaxIdLength
                || received.Kind is not (FleetRemovalKinds.Machine or FleetRemovalKinds.Grantee))
                continue;
            // A clock far ahead must not hold a machine out of the Fleet for good.
            var now = time.GetUtcNow();
            var removal = received with { RemovedAt = received.RemovedAt > now.AddMinutes(5) ? now : received.RemovedAt };

            if (removal.Kind == FleetRemovalKinds.Machine && removal.Id == self)
            {
                await LeftAsync(from, removal, callerDeviceId, cancellationToken);
                continue;
            }

            if (await repository.GetRemovalAsync(removal.Id) is { } known && known.RemovedAt >= removal.RemovedAt)
                continue;
            if (removal.Kind == FleetRemovalKinds.Machine)
            {
                var row = await repository.GetAsync(removal.Id);
                // Added after the removal: it was added back on purpose, and this is a stale copy. Neither kept nor passed on.
                if (row is not null && row.AddedAt > removal.RemovedAt)
                    continue;
                await repository.SaveRemovalAsync(removal);
                applied++;
                if (row is null)
                    await TellAllAsync([removal], from, cancellationToken);
                else
                    await ForgetLocallyAsync(row, removal, from, cancellationToken);
            }
            else
            {
                await repository.SaveRemovalAsync(removal);
                applied++;
                await RevokeGranteeLocallyAsync(removal.Id, cancellationToken);
                await TellAllAsync([removal], from, cancellationToken);
            }
        }

        return applied;
    }

    /// <summary>
    /// Compares removals with <paramref name="peer"/>: takes in what it has that this machine doesn't, and sends what it
    /// lacks. False when it can't be read.
    /// </summary>
    public async Task<bool> ExchangeWithAsync(RemoteMachine peer, CancellationToken cancellationToken)
    {
        var theirs = await FetchAsync(peer, cancellationToken);
        if (theirs is null)
            return false;
        await ApplyAsync(theirs, peer.Id, null, cancellationToken);

        // Never tell a machine it was forgotten by sending it its own old removal: it would drop the machine that sends it.
        var behind = (await repository.ListRemovalsAsync())
            .Where(mine => mine.Id != peer.Id && !theirs.Any(t => t.Id == mine.Id && t.RemovedAt >= mine.RemovedAt))
            .ToList();
        if (behind.Count > 0 && await repository.GetAsync(peer.Id) is { } still)
            await SendAsync(still, behind, cancellationToken);
        return true;
    }

    // Drops the machine here: its keys go, it's told, everybody else is told, and the row is deleted last.
    private async Task ForgetLocallyAsync(RemoteMachine machine, FleetRemoval removal, string? exceptPeerId, CancellationToken cancellationToken)
    {
        // The keys phones and screens hold on it, while this machine still has a key to remove them with; and the keys
        // other machines made for it at this one's request.
        await grants.RemoveGrantsOnAsync(machine.Id, cancellationToken);
        await grants.RevokeAllAsync(FleetRoster.MachinePrefix + machine.Id, cancellationToken);

        // It is told, and drops this machine and the key this machine uses on it.
        await SendAsync(machine, [removal], cancellationToken);
        await TellAllAsync([removal], exceptPeerId, cancellationToken, except: machine.Id);

        // The keys it uses to reach this machine.
        foreach (var key in (await devices.ListAsync()).Where(d => d.MadeFor == machine.Id))
            await devices.RevokeAsync(key.Id);

        await machines.DeleteAsync(machine.Id);
    }

    // This machine was forgotten by `from`: its keys on this machine stop working and it leaves this machine's list.
    private async Task LeftAsync(string? from, FleetRemoval removal, string? callerDeviceId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(from) || from == removal.Id)
            return;
        var row = await repository.GetAsync(from);
        // Added after it forgot this machine: a stale copy, not news.
        if (row is not null && row.AddedAt > removal.RemovedAt)
            return;

        foreach (var key in (await devices.ListAsync()).Where(d => d.MadeFor == from))
            await devices.RevokeAsync(key.Id);
        if (callerDeviceId is not null && (await devices.ListAsync()).Any(d => d.Id == callerDeviceId && d.Kind == DeviceKinds.Machine))
            await devices.RevokeAsync(callerDeviceId);
        if (row is not null)
        {
            // The phones' and browsers' keys made through this machine go; the keys it arranged between other machines stay.
            await grants.RemoveGrantsOnAsync(from, cancellationToken, keepMachineKeys: true);
            await machines.DeleteAsync(from);
        }

        LogLeft(logger, from);
    }

    private async Task RevokeGranteeLocallyAsync(string granteeId, CancellationToken cancellationToken)
    {
        // Its keys on other machines (made through this machine), and the keys other machines made for it on this one.
        await grants.RevokeAllAsync(granteeId, cancellationToken);
        var live = await devices.ListAsync();
        foreach (var key in live.Where(d => d.MadeFor == granteeId))
            await devices.RevokeAsync(key.Id);

        // The phone itself, when it's paired here.
        if (live.Any(d => d.Id == granteeId && d.Kind == DeviceKinds.Device) && await devices.RevokeAsync(granteeId))
        {
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IPushSubscriptionRepository>().DeleteByDeviceAsync(granteeId);
        }
    }

    private async Task TellAllAsync(IReadOnlyList<FleetRemoval> removals, string? exceptPeerId, CancellationToken cancellationToken, string? except = null)
    {
        var peers = (await repository.ListAsync()).Where(m => m.Id != exceptPeerId && m.Id != except && FleetRoster.IsPairable(m)).ToList();
        await Task.WhenAll(peers.Select(peer => SendAsync(peer, removals, cancellationToken)));
    }

    /// <summary>Tells a machine, as far as it answers. A machine that doesn't hears it the next time machines compare lists.</summary>
    private async Task<bool> SendAsync(RemoteMachine machine, IReadOnlyList<FleetRemoval> removals, CancellationToken cancellationToken)
    {
        if (machines.TryTokenOf(machine) is not { } token)
            return false;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(TellFor);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{machine.BaseUrl}/api/machine/removals")
            {
                Content = JsonContent.Create(new RemovalBatch(identity.Get().Id, removals), FleetRosterJsonContext.Default.RemovalBatch),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await httpClients.CreateClient(RemoteMachineService.HttpClientName).SendAsync(request, limit.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            LogTellFailed(logger, machine.Name);
            return false;
        }
    }

    private async Task<IReadOnlyList<FleetRemoval>?> FetchAsync(RemoteMachine machine, CancellationToken cancellationToken)
    {
        if (machines.TryTokenOf(machine) is not { } token)
            return null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{machine.BaseUrl}/api/machine/removals");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await httpClients.CreateClient(RemoteMachineService.HttpClientName).SendAsync(request, cancellationToken);
            // A machine too old to keep removals has none to share.
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return [];
            if (!response.IsSuccessStatusCode)
                return null;
            return (await response.Content.ReadFromJsonAsync(FleetRosterJsonContext.Default.RemovalList, cancellationToken))?.Removals;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Machine} forgot this machine; it left this machine's list.")]
    private static partial void LogLeft(ILogger logger, string machine);

    [LoggerMessage(Level = LogLevel.Information, Message = "Couldn't tell {Machine} about a removal; it hears when machines compare lists.")]
    private static partial void LogTellFailed(ILogger logger, string machine);
}

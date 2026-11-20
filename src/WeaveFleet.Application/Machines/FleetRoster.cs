using System.Net;
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

/// <summary>Where this machine can be reached from other machines (the addresses it reports in <c>GET /api/machine</c>).</summary>
public interface ISelfAddresses
{
    Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken);
}

/// <summary>What one machine tells another about a third (or about itself), with a key the third one made for it.</summary>
/// <param name="Since">When the introducing machine added the machine to its list; null when it introduces itself. A machine that was forgotten is only taken in again by an introduction made after it was forgotten.</param>
public sealed record Introduction(string MachineId, string Name, string? Os, IReadOnlyList<string> Addresses, string Token, string? IntroducedBy, DateTimeOffset? Since = null);

public enum IntroductionOutcome
{
    /// <summary>Saved in the list.</summary>
    Added,

    /// <summary>Already in the list; the key sent along wasn't used.</summary>
    Known,

    /// <summary>None of the addresses answered as that machine.</summary>
    Unreachable,

    /// <summary>The key or the machine didn't check out.</summary>
    Refused,
}

/// <summary>
/// Your Fleet: every machine keeps the whole list. Machines are introduced to each other instead of one machine
/// being home:
/// <list type="bullet">
/// <item>A machine <b>mints the key another machine uses to reach it</b> (a machine-kind device token on itself) and
/// never hands on a key it was given or its own owner token. Whoever introduces two machines asks each to make a key
/// for the other, with the machine token it already holds for both, and hands the keys over
/// (<c>POST /api/machine/introductions</c>).</item>
/// <item>Each machine, whenever its list changes or a machine in it answers again, compares its list with each
/// machine's (<see cref="SyncWithAsync"/>) and introduces the pairs that are missing. A machine that was off catches up
/// when it comes back, because everyone else then compares lists with it.</item>
/// <item>The keys an introduction made on another machine are remembered in <c>device_grants</c> under
/// <c>machine:{id}</c> (the machine they are for), and the introduced machine's row says who introduced it.</item>
/// </list>
/// A key that couldn't be delivered is removed again. Forgetting a machine reaches every machine
/// (<see cref="FleetRemovals"/>), and a list that is out of date doesn't introduce a forgotten machine again.
/// </summary>
public sealed partial class FleetRoster(
    IRemoteMachineRepository repository,
    RemoteMachineService machines,
    DeviceTokenService devices,
    IHttpClientFactory httpClients,
    MachineIdentityStore identity,
    ISelfAddresses selfAddresses,
    FleetRemovals removals,
    TimeProvider time,
    ILogger<FleetRoster> logger) : IDisposable
{
    /// <summary>The prefix of the grant row's device id for a key made for another machine.</summary>
    public const string MachinePrefix = "machine:";

    private readonly SemaphoreSlim _syncing = new(1, 1);

    public void Dispose() => _syncing.Dispose();

    /// <summary>Takes in a machine another machine introduces. Checks the machine answers as itself before the key is sent anywhere.</summary>
    public async Task<IntroductionOutcome> AcceptAsync(Introduction introduction, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(introduction.Token) || string.IsNullOrWhiteSpace(introduction.MachineId)
            || introduction.MachineId == identity.Get().Id)
            return IntroductionOutcome.Refused;
        if (await repository.GetAsync(introduction.MachineId) is not null)
            return IntroductionOutcome.Known;

        // A machine that was forgotten comes back only when it was added again after it was forgotten.
        var forgotten = await removals.GetAsync(introduction.MachineId) is { Kind: FleetRemovalKinds.Machine } gone ? gone : null;
        if (forgotten is not null && (introduction.Since is not { } since || since <= forgotten.RemovedAt))
            return IntroductionOutcome.Refused;

        foreach (var address in (introduction.Addresses ?? []).Select(RemoteMachineService.NormalizeBaseUrl).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // Who answers first, without the key: a key goes only to the machine it's for.
            if (!await AnswersAsAsync(address, introduction.MachineId, cancellationToken))
                continue;
            var (info, _) = await machines.IdentifyAsync(address, introduction.Token, cancellationToken);
            if (info is null || info.Id != introduction.MachineId)
                return IntroductionOutcome.Refused;
            var (machine, _) = await machines.AddAsync(address, introduction.Token, cancellationToken, introducedBy: introduction.IntroducedBy ?? "unknown");
            if (machine is not null && forgotten is not null)
                await repository.DeleteRemovalAsync(introduction.MachineId);
            return machine is null ? IntroductionOutcome.Refused : IntroductionOutcome.Added;
        }

        return IntroductionOutcome.Unreachable;
    }

    /// <summary>
    /// Compares this machine's list with <paramref name="machineId"/>'s and introduces what's missing: this machine to
    /// it, and every other machine here to it (and it to them). Does nothing when it doesn't answer; the next time it
    /// does, this runs again.
    /// </summary>
    public async Task SyncWithAsync(string machineId, CancellationToken cancellationToken)
    {
        if (await repository.GetAsync(machineId) is not { } peer)
            return;
        // What was forgotten or removed goes first: a list that still has it mustn't bring it back.
        if (!await removals.ExchangeWithAsync(peer, cancellationToken))
            return;
        if (await repository.GetAsync(machineId) is not { } current)
            return;
        peer = current;

        var known = await RosterOfAsync(peer, cancellationToken);
        if (known is null)
            return;

        var self = identity.Get();
        if (!known.Contains(self.Id))
            await IntroduceAsync(null, peer, cancellationToken);

        foreach (var other in await repository.ListAsync())
        {
            if (other.Id == peer.Id || known.Contains(other.Id) || !IsPairable(other) || !IsPairable(peer))
                continue;
            await IntroduceAsync(other, peer, cancellationToken);
            if (await RosterOfAsync(other, cancellationToken) is { } theirs && !theirs.Contains(peer.Id))
                await IntroduceAsync(peer, other, cancellationToken);
        }
    }

    /// <summary>Compares this machine's list with every machine in it, one after the other.</summary>
    public async Task SyncAllAsync(CancellationToken cancellationToken)
    {
        await _syncing.WaitAsync(cancellationToken);
        try
        {
            // What was forgotten or removed goes round first, to every machine, so nothing below introduces it again.
            foreach (var machine in await repository.ListAsync())
            {
                try
                {
                    if (await repository.GetAsync(machine.Id) is { } current)
                        await removals.ExchangeWithAsync(current, cancellationToken);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Security.Cryptography.CryptographicException)
                {
                    LogSyncFailed(logger, machine.Name);
                }
            }

            foreach (var machine in await repository.ListAsync())
            {
                try
                {
                    await SyncWithAsync(machine.Id, cancellationToken);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Security.Cryptography.CryptographicException)
                {
                    LogSyncFailed(logger, machine.Name);
                }
            }
        }
        finally
        {
            _syncing.Release();
        }
    }

    // A machine reached through an SSH tunnel is only reachable from here, so nobody else can be handed it.
    internal static bool IsPairable(RemoteMachine machine) => machine.SshDestination is null;

    /// <summary>
    /// Makes <paramref name="subject"/> (null: this machine) mint a key for <paramref name="target"/> and hands it
    /// over. A key that wasn't taken is removed again.
    /// </summary>
    private async Task<bool> IntroduceAsync(RemoteMachine? subject, RemoteMachine target, CancellationToken cancellationToken)
    {
        var self = identity.Get();
        var selfName = self.Name ?? Environment.MachineName;
        var keyName = Clip($"{target.Name} via {selfName}", 60);

        string deviceId, token, subjectId, subjectName;
        string? subjectOs;
        List<string> addresses;
        if (subject is null)
        {
            var (device, minted) = await devices.IssueAsync(keyName, "other", self.Id, DeviceKinds.Machine, madeFor: target.Id);
            (deviceId, token, subjectId, subjectName, subjectOs) = (device.Id, minted, self.Id, selfName, null);
            addresses = (await selfAddresses.ListAsync(cancellationToken)).ToList();
        }
        else
        {
            if (machines.TryTokenOf(subject) is not { } subjectToken)
                return false;
            var mint = await MintAsync(subject, subjectToken, keyName, self.Id, target.Id, cancellationToken);
            if (mint is null)
                return false;
            (deviceId, token, subjectId, subjectName, subjectOs) = (mint.Value.DeviceId, mint.Value.Token, subject.Id, subject.Name, subject.Os);
            addresses = AddressesOf(subject);
            await repository.SaveGrantAsync(new DeviceGrant { DeviceId = MachinePrefix + target.Id, MachineId = subject.Id, RemoteDeviceId = deviceId, CreatedAt = time.GetUtcNow() });
        }

        var outcome = addresses.Count == 0
            ? IntroductionOutcome.Unreachable
            : await DeliverAsync(target, new Introduction(subjectId, subjectName, subjectOs, addresses, token, self.Id, subject?.AddedAt), cancellationToken);
        if (outcome == IntroductionOutcome.Added)
            return true;

        // Nobody has the key: take it back.
        if (subject is null)
            await devices.RevokeAsync(deviceId);
        else if (await TryRemoveAsync(subject, deviceId, cancellationToken))
            await repository.DeleteGrantAsync(MachinePrefix + target.Id, subject.Id);
        return false;
    }

    private async Task<IntroductionOutcome> DeliverAsync(RemoteMachine target, Introduction introduction, CancellationToken cancellationToken)
    {
        if (machines.TryTokenOf(target) is not { } targetToken)
            return IntroductionOutcome.Refused;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{target.BaseUrl}/api/machine/introductions")
            {
                Content = JsonContent.Create(introduction, FleetRosterJsonContext.Default.Introduction),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", targetToken);
            using var response = await httpClients.CreateClient(RemoteMachineService.HttpClientName).SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return IntroductionOutcome.Refused;
            var body = await response.Content.ReadFromJsonAsync(FleetRosterJsonContext.Default.IntroductionAnswer, cancellationToken);
            return Enum.TryParse<IntroductionOutcome>(body?.Status, true, out var outcome) ? outcome : IntroductionOutcome.Refused;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return IntroductionOutcome.Unreachable;
        }
    }

    private async Task<(string DeviceId, string Token)?> MintAsync(RemoteMachine machine, string machineToken, string name, string pairedVia, string madeFor, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{machine.BaseUrl}/api/machine/devices")
            {
                Content = JsonContent.Create(new MintKeyRequest(name, "other", pairedVia, DeviceKinds.Machine, madeFor), FleetRosterJsonContext.Default.MintKeyRequest),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", machineToken);
            using var response = await httpClients.CreateClient(RemoteMachineService.HttpClientName).SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;
            var minted = await response.Content.ReadFromJsonAsync(FleetRosterJsonContext.Default.MintedKey, cancellationToken);
            return minted is { DeviceId.Length: > 0, Token.Length: > 0 } ? (minted.DeviceId, minted.Token) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private async Task<bool> TryRemoveAsync(RemoteMachine machine, string deviceId, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, $"{machine.BaseUrl}/api/machine/devices/{Uri.EscapeDataString(deviceId)}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", machines.TokenOf(machine));
            using var response = await httpClients.CreateClient(RemoteMachineService.HttpClientName).SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

    /// <summary>The ids in another machine's list, or null when it can't be read.</summary>
    private async Task<HashSet<string>?> RosterOfAsync(RemoteMachine machine, CancellationToken cancellationToken)
    {
        if (machines.TryTokenOf(machine) is not { } token)
            return null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{machine.BaseUrl}/api/machines");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await httpClients.CreateClient(RemoteMachineService.HttpClientName).SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return document.RootElement.TryGetProperty("machines", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(m => m.TryGetProperty("id", out var id) ? id.GetString() : null).OfType<string>().ToHashSet(StringComparer.Ordinal)
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private async Task<bool> AnswersAsAsync(string address, string machineId, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClients.CreateClient(RemoteMachineService.HttpClientName).GetAsync($"{address}/api/machine/id", cancellationToken);
            if (!response.IsSuccessStatusCode)
                return false;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return document.RootElement.TryGetProperty("id", out var id) && id.GetString() == machineId;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return false;
        }
    }

    // The addresses another machine can use to reach this one: what it reports, and the one in use. Loopback is only this machine's.
    private static List<string> AddressesOf(RemoteMachine machine) =>
        machine.Addresses.Select(a => a.Url).Append(machine.BaseUrl)
            .Where(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) && !uri.IsLoopback)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max].TrimEnd();

    [LoggerMessage(Level = LogLevel.Information, Message = "Couldn't compare lists with {Machine}; trying again when it answers.")]
    private static partial void LogSyncFailed(ILogger logger, string machine);
}

internal sealed record MintKeyRequest(string Name, string Platform, string PairedVia, string Kind, string MadeFor);

internal sealed record MintedKey(string DeviceId, string Token);

/// <summary>The answer to an introduction: an <see cref="IntroductionOutcome"/>.</summary>
public sealed record IntroductionAnswer(string Status);

[System.Text.Json.Serialization.JsonSerializable(typeof(Introduction))]
[System.Text.Json.Serialization.JsonSerializable(typeof(IntroductionAnswer))]
[System.Text.Json.Serialization.JsonSerializable(typeof(MintKeyRequest))]
[System.Text.Json.Serialization.JsonSerializable(typeof(MintedKey))]
[System.Text.Json.Serialization.JsonSerializable(typeof(RemovalBatch))]
[System.Text.Json.Serialization.JsonSerializable(typeof(RemovalList))]
[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class FleetRosterJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}

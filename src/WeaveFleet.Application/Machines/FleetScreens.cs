using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Devices;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Machines;

/// <summary>What one machine says about the phones and browsers that have keys on it (<c>GET /api/machine/screens/local</c>).</summary>
public sealed record LocalScreens(string MachineId, IReadOnlyList<DeviceSummary> Screens);

/// <summary>A machine that didn't answer, so its part of the list is missing.</summary>
public sealed record MachineRef(string Id, string Name);

/// <summary>One phone or browser in the Fleet, however many machines it has keys on.</summary>
/// <param name="Id">The phone's own device id, or <c>screen:{id}</c> for a browser. What Remove takes.</param>
/// <param name="ThroughId">The machine it connected through (its home): where it paired or signed in. Null when no machine says.</param>
/// <param name="Machines">The machines that hold a key for it.</param>
public sealed record FleetScreen(
    string Id,
    string Name,
    string? Platform,
    string? ThroughId,
    string? ThroughName,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastUsedAt,
    IReadOnlyList<string> Machines);

/// <summary>Every phone and browser in the Fleet, and the machines that didn't answer (<c>GET /api/machine/screens</c>).</summary>
public sealed record FleetScreenList(IReadOnlyList<FleetScreen> Screens, IReadOnlyList<MachineRef> NotAnswering);

/// <summary>
/// Your Fleet (Labs): the phones and browsers of the whole Fleet in one list. A phone or browser has one identity across
/// the Fleet: its device on the machine it paired with (or none, for a browser) and the keys other machines made for it
/// (<c>devices.made_for</c>). This machine asks every machine in its list for the devices it holds, then groups them by that
/// identity, so each shows once: its own name and platform, the machine it connected through, when it was last seen on any.
/// A machine that doesn't answer is named in <see cref="FleetScreenList.NotAnswering"/> and the rest still list.
/// </summary>
public sealed partial class FleetScreens(
    IRemoteMachineRepository repository,
    RemoteMachineService machines,
    DeviceTokenService devices,
    IHttpClientFactory httpClients,
    MachineIdentityStore identity,
    ILogger<FleetScreens> logger)
{
    private static readonly TimeSpan AskFor = TimeSpan.FromSeconds(5);

    /// <summary>What this machine holds itself: the phones and browsers, and the keys made for them. Another machine's key is left out.</summary>
    public async Task<LocalScreens> LocalAsync() =>
        new(identity.Get().Id, (await devices.ListAsync()).Where(d => d.Kind != DeviceKinds.Machine).ToList());

    public async Task<FleetScreenList> ListAsync(CancellationToken cancellationToken)
    {
        var self = identity.Get();
        var roster = await repository.ListAsync();
        var asked = await Task.WhenAll(roster.Select(machine => AskAsync(machine, cancellationToken)));

        var parts = new List<LocalScreens> { await LocalAsync() };
        var notAnswering = new List<MachineRef>();
        for (var i = 0; i < roster.Count; i++)
        {
            if (asked[i] is { } answer)
                // The machine's own id, as it says it: what its keys name as home.
                parts.Add(answer with { MachineId = roster[i].Id });
            else
                notAnswering.Add(new MachineRef(roster[i].Id, roster[i].Name));
        }

        var names = roster.ToDictionary(m => m.Id, m => m.Name, StringComparer.Ordinal);
        names[self.Id] = self.Name ?? Environment.MachineName;
        return new FleetScreenList(Merge(parts, names), notAnswering);
    }

    /// <summary>Groups what each machine holds by the phone or browser it is for.</summary>
    internal static IReadOnlyList<FleetScreen> Merge(IReadOnlyList<LocalScreens> parts, IReadOnlyDictionary<string, string> machineNames)
    {
        var entries = new List<(string Grantee, string MachineId, DeviceSummary Device)>();
        foreach (var part in parts)
            foreach (var device in part.Screens)
                entries.Add((device.MadeFor ?? device.Id, part.MachineId, device));

        var screens = new List<FleetScreen>();
        foreach (var group in entries.GroupBy(e => e.Grantee, StringComparer.Ordinal))
        {
            // Its own device (a phone paired where it was made) says who it is; a key made for it carries "{name} via {home}".
            var own = group.FirstOrDefault(e => e.Device.MadeFor is null);
            var through = own != default ? own.MachineId : group.Select(e => e.Device.PairedVia).FirstOrDefault(v => !string.IsNullOrEmpty(v));
            var throughName = through is not null && machineNames.TryGetValue(through, out var named) ? named : null;

            var source = own != default ? own : group.First();
            var name = source.Device.Name;
            if (own == default && throughName is not null && name.EndsWith($" via {throughName}", StringComparison.Ordinal))
                name = name[..^(" via ".Length + throughName.Length)];

            var platform = (own != default ? own.Device.Platform : null) ?? group.Select(e => e.Device.Platform).FirstOrDefault(p => !string.IsNullOrEmpty(p) && p != "other") ?? source.Device.Platform;
            screens.Add(new FleetScreen(
                group.Key,
                name,
                platform,
                through,
                throughName,
                group.Min(e => e.Device.CreatedAt),
                group.Max(e => e.Device.LastUsedAt),
                group.Select(e => e.MachineId).Distinct(StringComparer.Ordinal).ToList()));
        }

        return screens.OrderByDescending(s => s.LastUsedAt).ToList();
    }

    private async Task<LocalScreens?> AskAsync(RemoteMachine machine, CancellationToken cancellationToken)
    {
        if (machines.TryTokenOf(machine) is not { } token)
            return null;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(AskFor);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{machine.BaseUrl}/api/machine/screens/local");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await httpClients.CreateClient(RemoteMachineService.HttpClientName).SendAsync(request, limit.Token);
            if (!response.IsSuccessStatusCode)
                return null;
            return await response.Content.ReadFromJsonAsync(FleetScreensJsonContext.Default.LocalScreens, limit.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
        {
            LogNotAnswering(logger, machine.Name);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Machine} didn't answer for the list of phones and browsers.")]
    private static partial void LogNotAnswering(ILogger logger, string machine);
}

[System.Text.Json.Serialization.JsonSerializable(typeof(LocalScreens))]
[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class FleetScreensJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}

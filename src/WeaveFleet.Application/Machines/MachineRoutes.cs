using System.Net;
using System.Text.Json;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Machines;

/// <summary>
/// Which of a machine's addresses this Fleet talks to it on (<see cref="RemoteMachine.BaseUrl"/>, the address in use).
/// It starts with the one that worked last; when that doesn't answer it tries the others in their order, a quick probe
/// each (<see cref="ProbeTimeout"/>), the next starting before the last gives up (<see cref="Stagger"/>), and takes the
/// first in the order that answers. Every probe asks who answers (<c>GET /api/machine/id</c>, no token) and only an
/// address where the machine's own id answers is used, so its token never goes to a device that took over an old
/// address. While on a lower address, <see cref="BetterAsync"/> finds a better one that answers again; the watcher
/// moves there once its new connection is up. A machine with one address, or reached over SSH, keeps it as it is.
/// </summary>
public sealed class MachineRoutes(IRemoteMachineRepository machines, RemoteMachineService service, IHttpClientFactory httpClients, TimeProvider time) : IDisposable
{
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan Stagger = TimeSpan.FromMilliseconds(750);

    /// <summary>How often a connection on a lower address looks for a better one, and asks the machine its addresses.</summary>
    public static readonly TimeSpan RecheckEvery = TimeSpan.FromSeconds(60);

    // Reads and writes of a machine's list go one at a time, so a probe's result doesn't undo a reorder.
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>For tests: how long a probe waits and how far apart they start.</summary>
    internal TimeSpan Timeout { get; init; } = ProbeTimeout;

    internal TimeSpan Gap { get; init; } = Stagger;

    /// <summary>
    /// The address to connect on: the one in use when it answers as this machine, else the first in the order that
    /// does, else the one in use (so the caller's connect says why it can't).
    /// </summary>
    public async Task<string> PickAsync(string machineId, CancellationToken ct)
    {
        if (await machines.GetAsync(machineId) is not { } machine)
            return "";
        if (!HasChoices(machine))
            return machine.BaseUrl;

        var current = await ProbeAsync(machine.BaseUrl, machine.Id, ct);
        await RecordAsync(machine.Id, [(machine.BaseUrl, current)]);
        // An older Fleet there can't say who it is; the address it was added with is still trusted, as before.
        if (current is ProbeOutcome.Ok or ProbeOutcome.NoProbe)
            return machine.BaseUrl;

        var others = machine.Addresses.Select(address => address.Url).Where(url => !SameUrl(url, machine.BaseUrl)).ToList();
        var (winner, outcomes) = await RaceAsync(others, (url, token) => ProbeAsync(url, machine.Id, token), Gap, ct);
        await RecordAsync(machine.Id, outcomes);
        if (winner is null)
            return machine.BaseUrl;

        await UseAsync(machine.Id, winner);
        return winner;
    }

    /// <summary>
    /// An address above <paramref name="inUse"/> in the order where the machine answers now, or null. It doesn't move
    /// anything: the caller moves (<see cref="UseAsync"/>) once its connection there is up.
    /// </summary>
    public async Task<string?> BetterAsync(string machineId, string inUse, CancellationToken ct)
    {
        if (await machines.GetAsync(machineId) is not { } machine || !HasChoices(machine))
            return null;

        var above = machine.Addresses.TakeWhile(address => !SameUrl(address.Url, inUse)).Select(address => address.Url).ToList();
        if (above.Count == machine.Addresses.Count || above.Count == 0)
            return null;

        var (winner, outcomes) = await RaceAsync(above, (url, token) => ProbeAsync(url, machine.Id, token), Gap, ct);
        await RecordAsync(machine.Id, outcomes);
        return winner;
    }

    /// <summary>Whether <paramref name="url"/> is the machine's first address, so there's nothing better to look for.</summary>
    public async Task<bool> IsBestAsync(string machineId, string url)
        => await machines.GetAsync(machineId) is not { } machine || !HasChoices(machine) || SameUrl(machine.Addresses[0].Url, url);

    /// <summary>Makes <paramref name="url"/> the address in use; the next connection starts there.</summary>
    public async Task UseAsync(string machineId, string url)
    {
        await _gate.WaitAsync();
        try
        {
            if (await machines.GetAsync(machineId) is not { } machine)
                return;
            var addresses = MachineAddresses.Record(machine.Addresses, url, RemoteMachineAddressResults.Ok, time.GetUtcNow());
            await machines.UpdateAddressesAsync(machineId, url, addresses);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Asks the machine at <paramref name="url"/> (where it answered as itself) for its addresses now and learns them:
    /// new ones go in, learned ones it no longer has drop away. Not for a machine reached over SSH.
    /// </summary>
    public async Task LearnAsync(string machineId, string url, string token, CancellationToken ct)
    {
        if (await machines.GetAsync(machineId) is not { SshDestination: null } machine)
            return;
        var (info, _) = await service.IdentifyAsync(url, token, ct, machine.Name);
        if (info is null || info.Id != machineId || info.Addresses is null)
            return;

        await _gate.WaitAsync(ct);
        try
        {
            if (await machines.GetAsync(machineId) is not { } current)
                return;
            var learned = MachineAddresses.Learn(current.Addresses, info.Addresses, current.BaseUrl);
            if (!learned.SequenceEqual(current.Addresses))
                await machines.UpdateAddressesAsync(machineId, current.BaseUrl, learned);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Puts the machine's addresses in the owner's order. Null when the list changed since they read it.</summary>
    public async Task<RemoteMachine?> ReorderAsync(string machineId, IReadOnlyList<string> urls)
    {
        await _gate.WaitAsync();
        try
        {
            if (await machines.GetAsync(machineId) is not { } machine || MachineAddresses.Reorder(machine.Addresses, urls) is not { } reordered)
                return null;
            await machines.UpdateAddressesAsync(machineId, machine.BaseUrl, reordered);
            return machine with { Addresses = reordered };
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RecordAsync(string machineId, IEnumerable<(string Url, ProbeOutcome Outcome)> outcomes)
    {
        await _gate.WaitAsync();
        try
        {
            if (await machines.GetAsync(machineId) is not { } machine)
                return;
            var addresses = machine.Addresses;
            var now = time.GetUtcNow();
            foreach (var (url, outcome) in outcomes)
            {
                addresses = MachineAddresses.Record(addresses, url, outcome switch
                {
                    ProbeOutcome.Ok or ProbeOutcome.NoProbe => RemoteMachineAddressResults.Ok,
                    ProbeOutcome.OtherMachine => RemoteMachineAddressResults.OtherMachine,
                    _ => RemoteMachineAddressResults.NoAnswer,
                }, now);
            }
            await machines.UpdateAddressesAsync(machineId, machine.BaseUrl, addresses);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private static bool HasChoices(RemoteMachine machine) => machine.SshDestination is null && machine.Addresses.Count > 1;

    private static bool SameUrl(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Asks who answers at <paramref name="url"/>, without a token. <see cref="ProbeOutcome.NoProbe"/> is a Fleet too old
    /// to say, or something that isn't Fleet.
    /// </summary>
    internal async Task<ProbeOutcome> ProbeAsync(string url, string machineId, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            using var response = await httpClients.CreateClient(RemoteMachineService.HttpClientName)
                .GetAsync($"{url}/api/machine/id", timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound || !response.IsSuccessStatusCode
                || response.Content.Headers.ContentType?.MediaType != "application/json")
                return ProbeOutcome.NoProbe;

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            return document.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString() == machineId ? ProbeOutcome.Ok : ProbeOutcome.OtherMachine
                : ProbeOutcome.NoProbe;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return ProbeOutcome.NoAnswer;
        }
        catch (JsonException)
        {
            return ProbeOutcome.NoProbe;
        }
    }

    /// <summary>
    /// Probes <paramref name="urls"/> in order, each starting <paramref name="gap"/> after the one before or as soon as
    /// every one before it has failed, and returns the first in the order where the machine answered: a later one that
    /// answers sooner waits for those above it to finish. Also returns each probe's outcome. A probe only counts when it
    /// says <see cref="ProbeOutcome.Ok"/>; an old Fleet that can't say who it is isn't trusted at a new address.
    /// </summary>
    internal static async Task<(string? Winner, List<(string Url, ProbeOutcome Outcome)> Outcomes)> RaceAsync(
        IReadOnlyList<string> urls,
        Func<string, CancellationToken, Task<ProbeOutcome>> probe,
        TimeSpan gap,
        CancellationToken ct)
    {
        var outcomes = new List<(string, ProbeOutcome)>();
        if (urls.Count == 0)
            return (null, outcomes);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var running = new Task<ProbeOutcome>?[urls.Count];
        var results = new ProbeOutcome?[urls.Count];
        var started = 0;
        Task? nextStart = null;

        void Start()
        {
            running[started] = probe(urls[started], stop.Token);
            started++;
            nextStart = started < urls.Count ? Task.Delay(gap, stop.Token) : null;
        }

        try
        {
            Start();
            while (true)
            {
                // The first in the order still undecided; an answer below it waits for it.
                var first = Array.FindIndex(results, result => result is null);
                var winner = Array.FindIndex(results, result => result == ProbeOutcome.Ok);
                if (winner >= 0 && (first < 0 || winner < first))
                    return (urls[winner], Outcomes());
                if (first < 0)
                    return (null, Outcomes());
                if (first >= started)
                {
                    Start();
                    continue;
                }

                var waits = running.Take(started).Where((task, i) => task is not null && results[i] is null).Cast<Task>().ToList();
                if (nextStart is not null)
                    waits.Add(nextStart);
                var done = await Task.WhenAny(waits);
                if (done == nextStart)
                {
                    Start();
                    continue;
                }

                for (var i = 0; i < started; i++)
                {
                    if (results[i] is null && running[i]!.IsCompleted)
                        results[i] = running[i]!.IsCompletedSuccessfully ? running[i]!.Result : ProbeOutcome.NoAnswer;
                }
            }
        }
        finally
        {
            await stop.CancelAsync();
        }

        List<(string, ProbeOutcome)> Outcomes()
        {
            for (var i = 0; i < started; i++)
            {
                if (results[i] is { } result)
                    outcomes.Add((urls[i], result));
            }
            return outcomes;
        }
    }
}

/// <summary>What answered at an address.</summary>
public enum ProbeOutcome
{
    /// <summary>The machine itself.</summary>
    Ok,

    /// <summary>Nothing, or nothing in time.</summary>
    NoAnswer,

    /// <summary>Another Fleet: a different device has the address now.</summary>
    OtherMachine,

    /// <summary>Something that can't say who it is: a Fleet from before the probe, or not Fleet.</summary>
    NoProbe,
}

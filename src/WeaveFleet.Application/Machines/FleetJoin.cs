using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Devices;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Machines;

/// <summary>
/// One-time codes for a machine joining this Fleet from its own terminal (<c>install.sh --join</c>, <c>fleet node join</c>):
/// <c>J8RT-4WXN</c>, good for 30 minutes and for one machine. Unlike a pairing code (10 minutes) it is made ahead, pasted
/// into a server's terminal or start-up script, and sits there a while. In memory only, hashed, like a pairing code.
/// </summary>
public sealed class JoinCodeStore(TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    private readonly PairingCodeStore _codes = new(time, Lifetime);

    /// <summary>Makes a code. Only its typeable form is used; the secret half of a pairing code isn't part of a join line.</summary>
    public PairingCode Create() => _codes.Create();

    /// <summary>Whether the code is still good, without using it.</summary>
    public bool IsLive(string? code) => _codes.Peek(null, code) is not null;

    /// <summary>Uses the code up. Only one caller gets it.</summary>
    public bool TryConsume(string? code) => _codes.TryConsume(null, code) is not null;
}

/// <summary>
/// What <c>install.sh --join</c> is given: the inviting machine's addresses, then <c>#</c>, then the join code
/// (<c>http://100.64.1.7:6262,http://192.168.1.20:6262#J8RT-4WXN</c>). Several addresses are comma separated; the
/// joining machine tries them in order.
/// </summary>
public sealed record JoinTarget(IReadOnlyList<string> Addresses, string Code)
{
    public static string Format(IEnumerable<string> addresses, string code) => $"{string.Join(',', addresses)}#{code}";

    public static (JoinTarget? Target, string? Error) Parse(string? text)
    {
        var trimmed = text?.Trim().Trim('\'', '"');
        var hash = trimmed?.LastIndexOf('#') ?? -1;
        if (trimmed is null || hash <= 0 || hash == trimmed.Length - 1)
            return (null, "That isn't a join line. It looks like http://100.64.1.7:6262#J8RT-4WXN. Copy it from Add a machine → On a new server.");

        var code = PairingCodeStore.NormalizeManualCode(trimmed[(hash + 1)..]);
        if (code is null)
            return (null, "That isn't a join code. It looks like J8RT-4WXN. Copy the whole line from Add a machine → On a new server.");

        var addresses = trimmed[..hash].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(RemoteMachineService.NormalizeBaseUrl)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return addresses.Count == 0
            ? (null, "That isn't an address. A join line starts with the machine's address, such as http://100.64.1.7:6262.")
            : (new JoinTarget(addresses, $"{code[..4]}-{code[4..]}"), null);
    }
}

/// <summary>What a joining machine tells the Fleet it joins: its join code, where it listens, and a pairing code of its own to be added with.</summary>
public sealed record JoinRequest(string? Code, string? PairingCode, IReadOnlyList<string>? Addresses);

/// <summary>Who answered a join: the inviting machine, and whether it keeps a list (Your Fleet) that will bring this machine to the others.</summary>
public sealed record JoinAnswer(string MachineId, string Name, bool YourFleet);

public enum JoinStatus
{
    /// <summary>The Fleet added this machine.</summary>
    Joined,

    /// <summary>The code is wrong, expired or used.</summary>
    CodeRefused,

    /// <summary>Too many codes were tried; try again in a minute.</summary>
    TooManyTries,

    /// <summary>None of the Fleet's addresses answered.</summary>
    Unreachable,

    /// <summary>The Fleet couldn't reach this machine back, or said no for another reason.</summary>
    Refused,

    /// <summary>The line or this machine isn't fit to join.</summary>
    Invalid,
}

/// <param name="Listed">The Fleet's machine is in this machine's list too.</param>
/// <param name="FleetKeepsList">The Fleet keeps a list (Your Fleet), so the rest of it hears about this machine.</param>
public sealed record JoinResult(JoinStatus Status, string Message, string? FleetMachineId = null, string? FleetMachineName = null, bool Listed = false, bool FleetKeepsList = false);

/// <summary>
/// A machine joining a Fleet with a one-time code, from both ends. The inviting machine (<see cref="AcceptAsync"/>)
/// uses the code up and adds the machine that sent it, by the pairing code that machine made, the same way "Add a
/// machine" adds one: with a key of its own, and the master token stays where it is. Adding it is what brings it to the
/// rest of the Fleet (<see cref="FleetRoster"/> introduces it to every machine). The joining machine
/// (<see cref="JoinAsync"/>) makes that pairing code, sends it with the join code, and waits to be listed.
/// </summary>
public sealed class FleetJoin(
    JoinCodeStore joinCodes,
    PairingCodeStore pairingCodes,
    RemoteMachineService machines,
    IRemoteMachineRepository repository,
    MachineIdentityStore identity,
    IHttpClientFactory httpClients,
    TimeProvider time)
{
    public const string JoinPath = "/api/pairing/join";

    /// <summary>How long a machine that joined waits to see the Fleet's machine in its own list.</summary>
    public static readonly TimeSpan ListedWait = TimeSpan.FromSeconds(12);

    public const string CodeNotFound = "This join code has expired or was already used. Make a new one in Add a machine → On a new server.";

    /// <summary>The inviting machine: uses the code up, then adds the machine that asked.</summary>
    public async Task<(JoinStatus Status, string Message, JoinAnswer? Answer)> AcceptAsync(JoinRequest request, CancellationToken cancellationToken)
    {
        var addresses = (request.Addresses ?? []).Select(RemoteMachineService.NormalizeBaseUrl).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (addresses.Count == 0 || PairingCodeStore.NormalizeManualCode(request.PairingCode) is null)
            return (JoinStatus.Invalid, "A machine joining says where it listens and gives a pairing code of its own.", null);

        // Used up first: two machines with the same line never both join.
        if (!joinCodes.TryConsume(request.Code))
            return (JoinStatus.CodeRefused, CodeNotFound, null);

        string lastError = "";
        foreach (var address in addresses)
        {
            var (machine, error) = await machines.AddByCodeAsync(address, request.PairingCode!, cancellationToken);
            if (machine is null)
            {
                lastError = error?.Message ?? lastError;
                continue;
            }

            var self = identity.Get();
            return (JoinStatus.Joined, "", new JoinAnswer(self.Id, self.Name ?? Environment.MachineName, true));
        }

        var reason = lastError.Length == 0 ? "" : $"{lastError} ";
        return (JoinStatus.Unreachable,
            $"The code was right, but this machine couldn't reach the one that sent it ({string.Join(", ", addresses)}). {reason}The code is used up: make a new one in Add a machine → On a new server.",
            null);
    }

    /// <summary>The joining machine: sends the code to the Fleet, and waits to be listed there.</summary>
    /// <param name="ownAddresses">Where the Fleet can reach this machine back.</param>
    public async Task<JoinResult> JoinAsync(JoinTarget target, IReadOnlyList<string> ownAddresses, CancellationToken cancellationToken)
    {
        if (ownAddresses.Count == 0)
            return new JoinResult(JoinStatus.Invalid,
                "This machine only listens on itself, so the Fleet can't reach it. Run fleet node up and choose Tailscale or Local network, then join again.");

        var pairing = pairingCodes.Create();
        var body = new JoinRequest(target.Code, pairing.ManualCode, ownAddresses);
        string unreachable = "";

        foreach (var address in target.Addresses)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{address}{JoinPath}")
                {
                    Content = JsonContent.Create(body, FleetJoinJsonContext.Default.JoinRequest),
                };
                using var response = await httpClients.CreateClient(JoinHttpClientName).SendAsync(request, cancellationToken);
                if (response.StatusCode == HttpStatusCode.NotFound)
                    return new JoinResult(JoinStatus.CodeRefused, CodeNotFound);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    return new JoinResult(JoinStatus.TooManyTries, "Too many codes were tried. Wait a minute and try again.");
                if (!response.IsSuccessStatusCode)
                    return new JoinResult(JoinStatus.Refused, await ReadErrorAsync(response, cancellationToken));

                var answer = await response.Content.ReadFromJsonAsync(FleetJoinJsonContext.Default.JoinAnswer, cancellationToken);
                if (answer is null)
                    return new JoinResult(JoinStatus.Refused, $"{address} didn't answer like a Fleet.");

                // A Fleet on an older build keeps no list (it answers false), so it won't bring this machine to the others.
                var listed = answer.YourFleet && await WaitForListedAsync(answer.MachineId, cancellationToken);
                return new JoinResult(JoinStatus.Joined, "", answer.MachineId, answer.Name, listed, answer.YourFleet);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                unreachable = $"Couldn't reach {address}.";
            }
        }

        return new JoinResult(JoinStatus.Unreachable,
            $"{unreachable} Check that the Fleet you copied the line from is on, and that this machine is on its Tailscale or home network.");
    }

    public const string JoinHttpClientName = "FleetJoin";

    private async Task<bool> WaitForListedAsync(string machineId, CancellationToken cancellationToken)
    {
        var until = time.GetUtcNow() + ListedWait;
        while (true)
        {
            if (await repository.GetAsync(machineId) is not null)
                return true;
            if (time.GetUtcNow() >= until)
                return false;
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (document.RootElement.TryGetProperty("error", out var error) && error.GetString() is { Length: > 0 } message)
                return message;
        }
        catch (JsonException)
        {
        }

        return $"The Fleet answered {(int)response.StatusCode}.";
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(JoinRequest))]
[System.Text.Json.Serialization.JsonSerializable(typeof(JoinAnswer))]
[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class FleetJoinJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}

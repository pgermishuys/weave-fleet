using WeaveFleet.Application.Machines;

namespace WeaveFleet.Api.Contracts;

/// <summary>
/// A machine this Fleet knows. <paramref name="Token"/> is only sent to the owner; a paired device gets its own token
/// for a machine through a device grant instead.
/// </summary>
/// <param name="Status"><c>unknown</c>, <c>online</c>, <c>unreachable</c> or <c>unauthorized</c>, as this Fleet last saw it.</param>
/// <param name="AgentWork">Whether agents here can send it work: <c>ask</c> (the user is asked in the conversation), <c>always</c> or <c>never</c>.</param>
/// <param name="Diagnosis">Why this Fleet last couldn't reach it, while it can't; older clients ignore it.</param>
/// <param name="Addresses">Every address it can be reached at, in the order this Fleet tries them; <c>BaseUrl</c> is the one in use. Empty before it has reported any.</param>
public sealed record MachineEntryResponse(
    string Id,
    string Name,
    string BaseUrl,
    string? Os,
    string Status,
    DateTimeOffset AddedAt,
    DateTimeOffset? LastSeenAt,
    string? Token,
    string AgentWork,
    string? SshDestination = null,
    bool? TunnelUp = null,
    MachineDiagnosis? Diagnosis = null,
    IReadOnlyList<MachineAddressEntry>? Addresses = null);

/// <summary>One of a machine's addresses, in the order Fleet tries them.</summary>
/// <param name="Url">Base URL without a trailing slash.</param>
/// <param name="Kind"><c>tailnet</c> (its Tailscale name), <c>tailnet-ip</c>, <c>lan</c>, <c>hostname</c> or <c>other</c>.</param>
/// <param name="Learned">The machine reported it; false for the address it was added with.</param>
/// <param name="LastResult">What came of this Fleet's last try: <c>ok</c>, <c>no-answer</c> or <c>other-machine</c>; null before any.</param>
/// <param name="LastTriedAt">When that was.</param>
public sealed record MachineAddressEntry(string Url, string Kind, bool Learned, string? LastResult, DateTimeOffset? LastTriedAt);

public sealed record MachineListResponse(IReadOnlyList<MachineEntryResponse> Machines);

public sealed record AddMachineRequest(string? BaseUrl, string? Token);

/// <param name="AgentWork">Whether agents here can send it work: <c>ask</c>, <c>always</c> or <c>never</c>; left out, it stays as it is.</param>
/// <param name="Addresses">Every one of the machine's addresses, in the order to try them. Turned away when the list changed since it was read.</param>
public sealed record UpdateRemoteMachineRequest(string? BaseUrl, string? Token, string? Name, string? AgentWork = null, IReadOnlyList<string>? Addresses = null);

/// <summary>One machine from a browser's own list (<c>localStorage["weave:machines"]</c>).</summary>
public sealed record ImportMachineEntry(string? Id, string? Name, string? BaseUrl, string? Token, string? Os, DateTimeOffset? AddedAt);

public sealed record ImportMachinesRequest(IReadOnlyList<ImportMachineEntry>? Machines);

/// <summary>The phone's own token on another machine. Kept by the phone; home keeps only the other machine's id for it.</summary>
public sealed record DeviceGrantResponse(string MachineId, string BaseUrl, string Token);

/// <summary>A machine Fleet can already see (<c>~/.ssh/config</c> or Tailscale). <paramref name="Destination"/> is what to send back.</summary>
public sealed record MachineCandidateResponse(string Name, string Destination, string Source, string? Os);

/// <param name="Commands">Everything setting a machine up can run on it, in order.</param>
public sealed record MachineCandidateListResponse(IReadOnlyList<MachineCandidateResponse> Candidates, IReadOnlyList<string> Commands);

/// <param name="Destination">An <c>~/.ssh/config</c> alias or <c>user@host[:port]</c>.</param>
public sealed record AddSshMachineRequest(string? Destination);

/// <summary>What Fleet saw on a machine over SSH: <paramref name="State"/> is ready, noFleet, cantLogIn or unsupported.</summary>
public sealed record SshProbeResponse(string State, string? Os, string? Version, string? Error, MachineDiagnosis? Diagnosis);

/// <summary>A machine being set up: <paramref name="Status"/> is running, done or failed. <paramref name="Machine"/> is there when done.</summary>
public sealed record SetupJobResponse(string Id, string Destination, string Status, IReadOnlyList<SetupStep> Steps, MachineEntryResponse? Machine, string? Error, MachineDiagnosis? Diagnosis);

namespace WeaveFleet.Api.Contracts;

/// <summary>
/// A machine this Fleet knows. <paramref name="Token"/> is only sent to the owner; a paired device gets its own token
/// for a machine through a device grant instead.
/// </summary>
/// <param name="Status"><c>unknown</c>, <c>online</c>, <c>unreachable</c> or <c>unauthorized</c>, as this Fleet last saw it.</param>
public sealed record MachineEntryResponse(
    string Id,
    string Name,
    string BaseUrl,
    string? Os,
    string Status,
    DateTimeOffset AddedAt,
    DateTimeOffset? LastSeenAt,
    string? Token);

public sealed record MachineListResponse(IReadOnlyList<MachineEntryResponse> Machines);

public sealed record AddMachineRequest(string? BaseUrl, string? Token);

public sealed record UpdateRemoteMachineRequest(string? BaseUrl, string? Token, string? Name);

/// <summary>One machine from a browser's own list (<c>localStorage["weave:machines"]</c>).</summary>
public sealed record ImportMachineEntry(string? Id, string? Name, string? BaseUrl, string? Token, string? Os, DateTimeOffset? AddedAt);

public sealed record ImportMachinesRequest(IReadOnlyList<ImportMachineEntry>? Machines);

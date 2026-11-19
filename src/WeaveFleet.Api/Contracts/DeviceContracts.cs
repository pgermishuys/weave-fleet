using WeaveFleet.Api.Endpoints;
using WeaveFleet.Application.Devices;

namespace WeaveFleet.Api.Contracts;

/// <summary>Asks for a pairing code. <paramref name="BaseUrl"/> is an address of the caller's own to put first; leave it out and the code carries the addresses Fleet knows.</summary>
public sealed record CreatePairingRequest(string? BaseUrl);

/// <summary>
/// What a QR code carries, version 1: enough for any client (this web app, a native app later) to find the machine
/// and redeem the code. The web app puts it, base64url-encoded, in the fragment of <c>&lt;url&gt;/pair#p=…</c>, so it
/// never reaches a server or a log.
/// </summary>
/// <param name="Url">The first address, for a phone that reads only one.</param>
/// <param name="Urls">Every address the machine can be reached on, best first (<paramref name="Url"/> is the first). A phone tries each and checks the machine's id before it sends the secret. Absent from codes made by an older Fleet.</param>
public sealed record PairingPayload(int V, string MachineId, string MachineName, string Url, string Secret, IReadOnlyList<string>? Urls = null);

/// <summary>A new pairing code.</summary>
/// <param name="Secret">The one-time secret. Shown once; never stored in the clear.</param>
/// <param name="ManualCode">The same code in a form someone can type, <c>XXXX-XXXX</c>.</param>
/// <param name="Url">What the QR code encodes: <c>&lt;baseUrl&gt;/pair#p=&lt;base64url(payload)&gt;</c>.</param>
public sealed record CreatePairingResponse(
    string Secret,
    string ManualCode,
    DateTimeOffset ExpiresAt,
    string Url,
    PairingPayload Payload);

/// <summary>Names a pairing code by its secret (from the QR code) or its manual code (typed).</summary>
public sealed record PairingPreviewRequest(string? Secret, string? ManualCode);

/// <summary>The machine a pairing code would connect to.</summary>
public sealed record PairingPreviewResponse(string MachineId, string MachineName, string Os, DateTimeOffset ExpiresAt, string? Version = null);

/// <summary>Redeems a pairing code for a device token.</summary>
/// <param name="DeviceName">What the device is called in Settings: 1–60 characters.</param>
/// <param name="Platform"><c>ios</c>, <c>android</c> or <c>other</c>.</param>
/// <param name="Kind"><c>machine</c> when another Fleet redeems the code; leave it out for a phone or browser.</param>
public sealed record PairingRedeemRequest(string? Secret, string? ManualCode, string? DeviceName, string? Platform, string? Kind = null);

/// <summary>A pairing code for another Fleet to redeem (<c>fleet node pair</c>), and where to find this machine.</summary>
/// <param name="ManualCode">The code, <c>XXXX-XXXX</c>.</param>
/// <param name="Addresses">URLs another machine might use, best first. Empty when Fleet only listens on loopback.</param>
/// <param name="Link">The pairing link for the first address (<c>&lt;address&gt;/add-machine#CODE</c>); null when there is no address.</param>
/// <param name="PhoneLink">The same code as a link a phone scans (<c>&lt;address&gt;/pair#p=…</c>), carrying every address; null when there is none.</param>
public sealed record MachinePairingCodeResponse(
    string ManualCode,
    DateTimeOffset ExpiresAt,
    string MachineId,
    string MachineName,
    IReadOnlyList<string> Addresses,
    string? Link = null,
    string? PhoneLink = null);

/// <summary>Adds a machine by the address it listens on and a code from <c>fleet node pair</c>.</summary>
public sealed record PairMachineRequest(string? BaseUrl, string? Code);

/// <summary>The machine a pairing link points at, as it answered before the code was used.</summary>
/// <param name="Version">The Fleet version it runs; null for a Fleet too old to say.</param>
/// <param name="BaseUrl">The address as Fleet will use it.</param>
/// <summary>Why a machine couldn't be reached or added: <c>Error</c> is one line for older clients, <c>Diagnosis</c> names the step that broke.</summary>
public sealed record MachineErrorResponse(string Error, WeaveFleet.Application.Machines.MachineDiagnosis? Diagnosis);

/// <summary>What <c>POST /api/machines/{id}/check</c> found: <c>Diagnosis</c> is null when the machine answers.</summary>
public sealed record MachineCheckResponse(bool Reachable, string? Error, WeaveFleet.Application.Machines.MachineDiagnosis? Diagnosis);

public sealed record PairMachinePreviewResponse(string MachineId, string Name, string? Os, string? Version, string BaseUrl);

/// <summary>A paired device's token. Returned once; Fleet keeps only a hash.</summary>
public sealed record PairingRedeemResponse(string DeviceId, string Token, MachineResponse Machine);

/// <summary>Devices with access to this machine.</summary>
public sealed record DeviceListResponse(IReadOnlyList<DeviceSummary> Devices);

/// <summary>
/// Another machine asks this one for a device token on a phone's behalf (a device grant). Only the machine token
/// may ask.
/// </summary>
/// <param name="PairedVia">The id of the machine asking: the phone's home machine.</param>
/// <param name="Kind"><c>device</c> (the default) or <c>machine</c>: a key another machine uses to reach this one (Your Fleet, Labs).</param>
public sealed record CreateDeviceRequest(string? Name, string? Platform, string? PairedVia, string? Kind = null);

/// <summary>A device token made on request.</summary>
public sealed record CreateDeviceResponse(string DeviceId, string Token);

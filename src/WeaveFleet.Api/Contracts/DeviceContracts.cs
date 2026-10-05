using WeaveFleet.Api.Endpoints;
using WeaveFleet.Application.Devices;

namespace WeaveFleet.Api.Contracts;

/// <summary>Asks for a pairing code. <paramref name="BaseUrl"/> is the address the phone should open.</summary>
public sealed record CreatePairingRequest(string? BaseUrl);

/// <summary>
/// What a QR code carries, version 1: enough for any client (this web app, a native app later) to find the machine
/// and redeem the code. The web app puts it, base64url-encoded, in the fragment of <c>&lt;url&gt;/pair#p=…</c>, so it
/// never reaches a server or a log.
/// </summary>
public sealed record PairingPayload(int V, string MachineId, string MachineName, string Url, string Secret);

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
public sealed record PairingPreviewResponse(string MachineId, string MachineName, string Os, DateTimeOffset ExpiresAt);

/// <summary>Redeems a pairing code for a device token.</summary>
/// <param name="DeviceName">What the device is called in Settings: 1–60 characters.</param>
/// <param name="Platform"><c>ios</c>, <c>android</c> or <c>other</c>.</param>
public sealed record PairingRedeemRequest(string? Secret, string? ManualCode, string? DeviceName, string? Platform);

/// <summary>A paired device's token. Returned once; Fleet keeps only a hash.</summary>
public sealed record PairingRedeemResponse(string DeviceId, string Token, MachineResponse Machine);

/// <summary>Devices with access to this machine.</summary>
public sealed record DeviceListResponse(IReadOnlyList<DeviceSummary> Devices);

/// <summary>
/// Another machine asks this one for a device token on a phone's behalf (a device grant). Only the machine token
/// may ask.
/// </summary>
/// <param name="PairedVia">The id of the machine asking: the phone's home machine.</param>
public sealed record CreateDeviceRequest(string? Name, string? Platform, string? PairedVia);

/// <summary>A device token made on request.</summary>
public sealed record CreateDeviceResponse(string DeviceId, string Token);

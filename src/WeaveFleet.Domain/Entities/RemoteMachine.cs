namespace WeaveFleet.Domain.Entities;

/// <summary>Another Fleet this one knows: where it is, and its access token (encrypted).</summary>
public sealed record RemoteMachine
{
    /// <summary>The machine's own id, from its <c>GET /api/machine</c>.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>
    /// The address in use: base URL without a trailing slash, e.g. <c>https://falcon.tail9c2e.ts.net</c>. One of
    /// <see cref="Addresses"/> when it has any; Fleet moves it to whichever answers first in their order.
    /// </summary>
    public required string BaseUrl { get; init; }

    /// <summary>
    /// Every address it can be reached at, in the order Fleet tries them: the one it was added with, and those it
    /// reports itself. Empty for a machine saved before Fleet kept them, or one reached over SSH.
    /// </summary>
    public IReadOnlyList<RemoteMachineAddress> Addresses { get; init; } = [];

    /// <summary>Its access token, encrypted with Data Protection. Never sent to a paired device.</summary>
    public required string EncryptedToken { get; init; }

    public string? Os { get; init; }

    public DateTimeOffset AddedAt { get; init; }

    public DateTimeOffset? LastSeenAt { get; init; }

    /// <summary>One of <see cref="RemoteMachineStatuses"/>.</summary>
    public string Status { get; init; } = RemoteMachineStatuses.Unknown;

    /// <summary>
    /// Whether agents in sessions here may send it work (start, message and read sessions there): one of
    /// <see cref="MachineAgentWork"/>. A machine starts at <see cref="MachineAgentWork.Ask"/>.
    /// </summary>
    public string AgentWork { get; init; } = MachineAgentWork.Ask;

    /// <summary>
    /// Set when the machine was added Over SSH: an <c>~/.ssh/config</c> alias or <c>user@host[:port]</c>. Fleet keeps a
    /// tunnel to it open and <see cref="BaseUrl"/> is the tunnel's local address. Null otherwise.
    /// </summary>
    public string? SshDestination { get; init; }
}

/// <summary>One address of a <see cref="RemoteMachine"/>.</summary>
/// <param name="Url">Base URL without a trailing slash.</param>
/// <param name="Kind"><c>tailnet</c>, <c>tailnet-ip</c>, <c>lan</c>, <c>hostname</c> or <c>other</c>.</param>
/// <param name="Learned">The machine reported it; false for the address it was added with.</param>
/// <param name="LastResult">What came of the last try: <c>ok</c>, <c>no-answer</c> or <c>other-machine</c>.</param>
/// <param name="LastTriedAt">When that was.</param>
public sealed record RemoteMachineAddress(string Url, string Kind, bool Learned, string? LastResult = null, DateTimeOffset? LastTriedAt = null);

public static class RemoteMachineAddressResults
{
    public const string Ok = "ok";
    public const string NoAnswer = "no-answer";

    /// <summary>Something answered, but not this machine: another device has the address now. No token went there.</summary>
    public const string OtherMachine = "other-machine";
}

/// <summary>"Agents here can send it work", a machine's one setting for agents handing work to it.</summary>
public static class MachineAgentWork
{
    /// <summary>The user is asked in the conversation the first time an agent sends it work; their answer can save Always or Never.</summary>
    public const string Ask = "ask";

    /// <summary>Agents send it work without asking.</summary>
    public const string Always = "always";

    /// <summary>Agents can't send it work, and don't see it.</summary>
    public const string Never = "never";

    public static bool IsKnown(string? value) => value is Ask or Always or Never;

    /// <summary>A stored value, with anything Fleet doesn't know read as <see cref="Ask"/>.</summary>
    public static string Read(string? value) => IsKnown(value) ? value! : Ask;
}

public static class RemoteMachineStatuses
{
    public const string Unknown = "unknown";
    public const string Online = "online";
    public const string Unreachable = "unreachable";

    /// <summary>It turned the token away: someone replaced it there. Not retried until the token changes.</summary>
    public const string Unauthorized = "unauthorized";
}

/// <summary>A device token another machine made for one of this machine's paired devices.</summary>
public sealed record DeviceGrant
{
    /// <summary>The paired device here.</summary>
    public required string DeviceId { get; init; }

    /// <summary>The other machine.</summary>
    public required string MachineId { get; init; }

    /// <summary>The other machine's id for the device it made.</summary>
    public required string RemoteDeviceId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Set when the device was removed here but not yet there.</summary>
    public DateTimeOffset? RevokedAt { get; init; }
}

using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Application.Devices;

/// <summary>
/// The wire format of a device token: <c>fdt_&lt;deviceId&gt;.&lt;secret&gt;</c>, where the device id is a ULID and
/// the secret is 32 random bytes, base64url without padding. Clients treat it as opaque.
/// </summary>
public static class DeviceToken
{
    public const string Prefix = "fdt_";

    private const int SecretBytes = 32;

    /// <summary>True when <paramref name="presented"/> claims to be a device token, rightly or not.</summary>
    public static bool HasPrefix(string presented) => presented.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Makes a token for <paramref name="deviceId"/> and returns it with the hash Fleet keeps.</summary>
    public static (string Token, byte[] Hash) Create(string deviceId)
    {
        Span<byte> secret = stackalloc byte[SecretBytes];
        RandomNumberGenerator.Fill(secret);
        var encoded = Base64Url(secret);
        return ($"{Prefix}{deviceId}.{encoded}", Hash(encoded));
    }

    /// <summary>Splits a token into its device id and secret. False for anything that isn't shaped like one.</summary>
    public static bool TryParse(string presented, [NotNullWhen(true)] out string? deviceId, [NotNullWhen(true)] out string? secret)
    {
        deviceId = null;
        secret = null;
        if (!HasPrefix(presented))
            return false;

        var body = presented.AsSpan(Prefix.Length);
        var dot = body.IndexOf('.');
        if (dot <= 0 || dot == body.Length - 1)
            return false;

        var id = body[..dot];
        var rest = body[(dot + 1)..];
        if (!Ulid.TryParse(id, out _) || rest.Length is < 40 or > 64 || !IsBase64Url(rest))
            return false;

        deviceId = id.ToString();
        secret = rest.ToString();
        return true;
    }

    /// <summary>SHA-256 of the secret's characters, the form Fleet stores.</summary>
    public static byte[] Hash(string secret) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret));

    private static bool IsBase64Url(ReadOnlySpan<char> value)
    {
        foreach (var c in value)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
                return false;
        }

        return true;
    }

    private static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>A device token that checked out.</summary>
public sealed record DeviceValidation(string DeviceId, string Name);

/// <summary>A device as Settings lists it. Never carries the hash.</summary>
public sealed record DeviceSummary(
    string Id,
    string Name,
    string? Platform,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastUsedAt,
    string? PairedVia)
{
    public static DeviceSummary From(Device device) =>
        new(device.Id, device.Name, device.Platform, device.CreatedAt, device.LastUsedAt, device.PairedVia);
}

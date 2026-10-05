using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WeaveFleet.Application.Configuration;

/// <summary>
/// This machine's VAPID key pair: how push services know pushes come from this Fleet. Made on first use and kept in
/// <c>&lt;db name&gt;.push.json</c> beside the database, readable only by the current user on Unix, like
/// <see cref="MachineIdentityStore"/>. Changing it would orphan every phone's subscription, so it never changes.
/// </summary>
public sealed class VapidKeyStore(string databasePath)
{
    private readonly object _gate = new();
    private VapidKeys? _keys;

    public string FilePath { get; } = Path.ChangeExtension(Path.GetFullPath(databasePath), ".push.json");

    public VapidKeys Get()
    {
        lock (_gate)
        {
            if (_keys is not null)
                return _keys;

            var loaded = Read();
            if (loaded is not null && IsValid(loaded))
                return _keys = loaded;

            var created = Generate();
            Write(created);
            return _keys = created;
        }
    }

    /// <summary>A new P-256 key pair: the public key uncompressed (65 bytes), the private scalar (32 bytes), base64url.</summary>
    public static VapidKeys Generate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = key.ExportParameters(includePrivateParameters: true);
        var publicKey = new byte[65];
        publicKey[0] = 0x04;
        parameters.Q.X!.CopyTo(publicKey, 1);
        parameters.Q.Y!.CopyTo(publicKey, 33);
        return new VapidKeys(Base64Url.Encode(publicKey), Base64Url.Encode(parameters.D!), DateTimeOffset.UtcNow);
    }

    private static bool IsValid(VapidKeys keys)
    {
        try
        {
            return Base64Url.Decode(keys.PublicKey).Length == 65 && Base64Url.Decode(keys.PrivateKey).Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private VapidKeys? Read()
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(FilePath), VapidKeysJsonContext.Default.VapidKeys);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private void Write(VapidKeys keys)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporaryPath = FilePath + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        using (var stream = new FileStream(temporaryPath, options))
        using (var writer = new StreamWriter(stream))
            writer.Write(JsonSerializer.Serialize(keys, VapidKeysJsonContext.Default.VapidKeys));

        File.Move(temporaryPath, FilePath, overwrite: true);
    }
}

/// <summary>A VAPID key pair, base64url (no padding).</summary>
public sealed record VapidKeys(string PublicKey, string PrivateKey, DateTimeOffset CreatedAt);

/// <summary>base64url without padding, as Web Push uses everywhere.</summary>
public static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string value)
    {
        var padded = value.Trim().Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }
}

[JsonSerializable(typeof(VapidKeys))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
internal sealed partial class VapidKeysJsonContext : JsonSerializerContext
{
}

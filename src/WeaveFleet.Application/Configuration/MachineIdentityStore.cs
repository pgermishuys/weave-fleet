using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WeaveFleet.Application.Configuration;

/// <summary>
/// Who this Fleet is to other devices: an id that survives restarts, port changes and renames, the name people
/// see, and the access token a client pastes to reach it. Kept in <c>&lt;db name&gt;.machine.json</c> next to the
/// database, so each data directory is its own machine. Readable only by the current user on Unix; the database
/// beside it already holds provider credentials, so the token adds nothing an attacker there doesn't have.
/// </summary>
public sealed class MachineIdentityStore
{
    private readonly object _gate = new();
    private MachineIdentity? _identity;

    public MachineIdentityStore(string databasePath)
    {
        FilePath = GetFilePath(databasePath);
    }

    public string FilePath { get; }

    public static string GetFilePath(string databasePath) =>
        Path.ChangeExtension(Path.GetFullPath(databasePath), ".machine.json");

    /// <summary>The identity, created and saved on first use.</summary>
    public MachineIdentity Get()
    {
        lock (_gate)
        {
            if (_identity is not null)
                return _identity;

            var loaded = Read();
            if (loaded is not null && IsValid(loaded))
                return _identity = loaded;

            // A missing or unreadable file gets a fresh identity. Keep whatever part of a damaged one still holds.
            var created = new MachineIdentity(
                loaded is not null && Guid.TryParse(loaded.Id, out _) ? loaded.Id : Guid.NewGuid().ToString("N"),
                loaded?.Name,
                loaded?.AccessToken is { Length: >= MinimumTokenLength } token ? token : NewToken());
            Write(created);
            return _identity = created;
        }
    }

    /// <summary>Applies <paramref name="change"/> and saves the result.</summary>
    public MachineIdentity Update(Func<MachineIdentity, MachineIdentity> change)
    {
        lock (_gate)
        {
            var next = change(Get());
            Write(next);
            return _identity = next;
        }
    }

    /// <summary>The shortest token Fleet accepts, from the environment or the file.</summary>
    public const int MinimumTokenLength = 16;

    /// <summary>A new random access token: 32 bytes, URL-safe, so it can ride in a WebSocket query string.</summary>
    public static string NewToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static bool IsValid(MachineIdentity identity) =>
        Guid.TryParse(identity.Id, out _) && identity.AccessToken is { Length: >= MinimumTokenLength };

    private MachineIdentity? Read()
    {
        try
        {
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize(json, MachineIdentityJsonContext.Default.MachineIdentity);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private void Write(MachineIdentity identity)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

        var json = JsonSerializer.Serialize(identity, MachineIdentityJsonContext.Default.MachineIdentity);
        var temporaryPath = FilePath + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        using (var stream = new FileStream(temporaryPath, options))
        using (var writer = new StreamWriter(stream))
            writer.Write(json);

        File.Move(temporaryPath, FilePath, overwrite: true);
    }
}

/// <summary>What <see cref="MachineIdentityStore"/> keeps.</summary>
/// <param name="Id">Stable id, a GUID without dashes. Never changes once made.</param>
/// <param name="Name">The name someone gave this machine, or null to use the host name.</param>
/// <param name="AccessToken">The token other devices use to reach this Fleet, unless the environment fixes one.</param>
public sealed record MachineIdentity(string Id, string? Name, string? AccessToken);

[JsonSerializable(typeof(MachineIdentity))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
internal sealed partial class MachineIdentityJsonContext : JsonSerializerContext
{
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace WeaveFleet.Application.Configuration;

/// <summary>
/// Whether the desktop app lets other devices reach it, and how. Kept in <c>&lt;db name&gt;.reach.json</c> next to the
/// database, so the choice survives restarts and belongs to the data directory (the app's own Fleet). A missing or
/// unreadable file means <see cref="ReachModes.Off"/>: the app is reachable from this machine only until someone asks.
/// </summary>
public sealed class ReachStore
{
    private readonly object _gate = new();

    public ReachStore(string databasePath)
    {
        FilePath = Path.ChangeExtension(Path.GetFullPath(databasePath), ".reach.json");
    }

    public string FilePath { get; }

    public ReachSettings Get()
    {
        lock (_gate)
        {
            try
            {
                var json = File.ReadAllText(FilePath);
                var read = JsonSerializer.Deserialize(json, ReachJsonContext.Default.ReachSettings);
                return read is not null && ReachModes.IsKnown(read.Mode) ? read : ReachSettings.Off;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return ReachSettings.Off;
            }
        }
    }

    public void Save(ReachSettings settings)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var json = JsonSerializer.Serialize(settings, ReachJsonContext.Default.ReachSettings);
            var temporaryPath = $"{FilePath}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, FilePath, overwrite: true);
        }
    }
}

/// <summary>The ways the desktop app can be reached. Plain strings, since they are stored and sent as they are.</summary>
public static class ReachModes
{
    /// <summary>This machine only: the loopback listener the window uses.</summary>
    public const string Off = "off";

    /// <summary>Also on this machine's Tailscale address and its home network addresses.</summary>
    public const string TailscaleAndHome = "tailscale-and-home";

    /// <summary>Also on this machine's home network addresses, not Tailscale.</summary>
    public const string Home = "home";

    /// <summary>A second loopback port that always asks for a key, for a tunnel or a proxy the person runs.</summary>
    public const string Own = "own";

    public static bool IsKnown(string? mode) => mode is Off or TailscaleAndHome or Home or Own;
}

/// <param name="Mode">One of <see cref="ReachModes"/>.</param>
/// <param name="OwnAddress">For <see cref="ReachModes.Own"/>: the address the tunnel or proxy answers at, e.g. <c>https://fleet.example.com</c>.</param>
/// <param name="OwnPort">For <see cref="ReachModes.Own"/>: the loopback port the tunnel or proxy sends to. Chosen once and kept.</param>
public sealed record ReachSettings(string Mode, string? OwnAddress = null, int? OwnPort = null)
{
    public static ReachSettings Off { get; } = new(ReachModes.Off);
}

[JsonSerializable(typeof(ReachSettings))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class ReachJsonContext : JsonSerializerContext
{
}

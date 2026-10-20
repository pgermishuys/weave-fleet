using System.Text.Json;
using System.Text.Json.Serialization;

namespace WeaveFleet.Application.Updates;

/// <summary>When a Fleet restarts itself into an update it has downloaded.</summary>
public static class UpdateRules
{
    /// <summary>Waits for the user.</summary>
    public const string Ask = "ask";

    /// <summary>Restarts once no session has worked for a minute.</summary>
    public const string Idle = "idle";

    /// <summary>Like <see cref="Idle"/>, but only between 02:00 and 05:00 on this machine's clock.</summary>
    public const string Overnight = "overnight";

    public static bool IsKnown(string? rule) => rule is Ask or Idle or Overnight;
}

/// <summary>The update this Fleet last restarted into, and what started the restart: <c>idle</c>, <c>overnight</c>, <c>manual</c> or <c>rollout</c>.</summary>
public sealed record LastUpdate(
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("to")] string To,
    [property: JsonPropertyName("at")] string At,
    [property: JsonPropertyName("trigger")] string Trigger);

/// <summary>
/// What a Fleet remembers about updating itself, in <c>&lt;db name&gt;.update.json</c> next to the database: the rule it
/// follows (null until someone gives it one), the restart it is in the middle of, and the update it last made. A node
/// acts on its rule alone, so the rule lives here even when the home Fleet that set it is off.
/// </summary>
public sealed class UpdateRuleStore(string filePath)
{
    private readonly object _gate = new();
    private State? _state;

    public static string GetFilePath(string databasePath) => Path.ChangeExtension(Path.GetFullPath(databasePath), ".update.json");

    /// <summary>The rule someone set, or null for the default (<see cref="UpdateRules.Ask"/>).</summary>
    public string? Rule => Read().Rule;

    /// <summary>The update this Fleet last restarted into; kept until the next one.</summary>
    public LastUpdate? Last => Read().Last;

    public void SetRule(string? rule) => Change(state => state with { Rule = rule });

    /// <summary>Written before exiting: the restart that is about to happen and why.</summary>
    public void RecordRestart(LastUpdate restart) => Change(state => state with { Pending = restart });

    /// <summary>The new version is up: the restart it was started by becomes the last update. A different version came back (the update was undone) forgets it.</summary>
    public void CompleteUpdate(string currentVersion) => Change(state => state.Pending is null
        ? state
        : state with { Pending = null, Last = string.Equals(state.Pending.To, currentVersion, StringComparison.OrdinalIgnoreCase) ? state.Pending : state.Last });

    private State Read()
    {
        lock (_gate)
            return _state ??= Load();
    }

    private void Change(Func<State, State> change)
    {
        lock (_gate)
        {
            var next = change(_state ??= Load());
            if (next == _state)
                return;

            _state = next;
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            var temporary = filePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(next, UpdateRuleJsonContext.Default.State));
            File.Move(temporary, filePath, overwrite: true);
        }
    }

    private State Load()
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(filePath), UpdateRuleJsonContext.Default.State) ?? new State();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new State();
        }
    }

    internal sealed record State(string? Rule = null, LastUpdate? Pending = null, LastUpdate? Last = null);
}

[JsonSerializable(typeof(UpdateRuleStore.State))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class UpdateRuleJsonContext : JsonSerializerContext
{
}

namespace WeaveFleet.Application.Mods.Host;

/// <summary>
/// Fleet's own count of each mod's failures in a row (<c>docs/mods/api.md</c>, "Three strikes"). The host counts too, but
/// its counts die with it; this one lasts across restarts. Safe to use from any thread.
/// </summary>
internal sealed class ModStrikes
{
    /// <summary>Failures in a row that turn a mod off.</summary>
    public const int Limit = 3;

    private readonly Lock _sync = new();
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

    /// <summary>A hook, timer or callback failed; <paramref name="hostStrikes"/> is the host's count. Returns the new count.</summary>
    public int Failed(string modId, int hostStrikes)
    {
        lock (_sync)
            return _counts[modId] = Math.Max(_counts.GetValueOrDefault(modId) + 1, hostStrikes);
    }

    /// <summary>The mod was running when the host stopped answering. Returns the new count.</summary>
    public int Hung(string modId)
    {
        lock (_sync)
            return _counts[modId] = _counts.GetValueOrDefault(modId) + 1;
    }

    /// <summary>A hook of the mod ran without failing, or the mod loaded again: the count starts over.</summary>
    public void Reset(string modId)
    {
        lock (_sync)
            _counts.Remove(modId);
    }

    public int Of(string modId)
    {
        lock (_sync)
            return _counts.GetValueOrDefault(modId);
    }
}

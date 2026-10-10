namespace WeaveFleet.Application.Mods.Host;

/// <summary>
/// Fleet's own count of each mod's failures in a row (<c>docs/mods/api.md</c>, "Three strikes"). The host counts too, but
/// its counts die with it; this one lasts across restarts. Safe to use from any thread.
/// </summary>
internal sealed class ModStrikes
{
    /// <summary>Failures in a row that turn a mod off.</summary>
    public const int Limit = 3;

    /// <summary>A hook, timer or callback failed; <paramref name="hostStrikes"/> is the host's count. Returns the new count.</summary>
    public int Failed(string modId, int hostStrikes) => throw new NotImplementedException();

    /// <summary>The mod was running when the host stopped answering. Returns the new count.</summary>
    public int Hung(string modId) => throw new NotImplementedException();

    /// <summary>A hook of the mod ran without failing, or the mod loaded again: the count starts over.</summary>
    public void Reset(string modId) => throw new NotImplementedException();

    public int Of(string modId) => throw new NotImplementedException();
}

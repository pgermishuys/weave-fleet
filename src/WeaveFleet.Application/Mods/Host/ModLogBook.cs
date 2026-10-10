namespace WeaveFleet.Application.Mods.Host;

/// <summary>
/// Each mod's last log lines, per user and mod id: what <c>$.ui.log</c>, <c>console</c>, failures and refused loads wrote.
/// Keeps the last <see cref="Capacity"/> lines of each, in memory. Safe to use from any thread.
/// </summary>
public sealed class ModLogBook
{
    /// <summary>How many lines each mod keeps; older ones drop off.</summary>
    public const int Capacity = 200;

    public void Add(string userId, string modId, ModLogLine line) => throw new NotImplementedException();

    /// <summary>The mod's lines, oldest first; empty when it has none.</summary>
    public IReadOnlyList<ModLogLine> Read(string userId, string modId) => throw new NotImplementedException();
}

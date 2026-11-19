namespace WeaveFleet.Domain.Entities;

/// <summary>
/// Something that was forgotten or removed from the whole Fleet (Your Fleet, Labs), kept so a machine that was off, or
/// holds a stale list, can't bring it back.
/// </summary>
/// <param name="Id">A machine's id, or a phone's or screen's id (<c>screen:{id}</c>).</param>
/// <param name="Kind">One of <see cref="FleetRemovalKinds"/>.</param>
/// <param name="RemovedAt">When it was removed, by the clock of the machine that did it.</param>
/// <param name="RemovedBy">The id of the machine the user removed it on.</param>
public sealed record FleetRemoval(string Id, string Kind, DateTimeOffset RemovedAt, string? RemovedBy);

public static class FleetRemovalKinds
{
    /// <summary>A machine forgotten from the Fleet.</summary>
    public const string Machine = "machine";

    /// <summary>A phone or a browser removed from the Fleet: its keys on every machine are revoked and it gets none again.</summary>
    public const string Grantee = "grantee";
}

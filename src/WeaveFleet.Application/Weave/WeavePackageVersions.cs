namespace WeaveFleet.Application.Weave;

/// <summary>Which version of a Weave package Fleet writes into a harness's config when it adds Weave.</summary>
public interface IWeavePackageVersions
{
    /// <summary>
    /// The newest of the package's <c>latest</c> and <c>next</c> releases on npm, or <see langword="null"/> when npm
    /// couldn't be asked. Weave's <c>latest</c> has lagged behind <c>next</c> (0.1.x, which predates both the OpenCode 2
    /// agents and the config folder Fleet hands Weave), so a bare package name isn't good enough.
    /// </summary>
    Task<string?> NewestAsync(string package, CancellationToken ct);
}

/// <summary>Semantic versions with pre-releases: <c>0.2.0-next.3</c> is newer than <c>0.1.0</c> and older than <c>0.2.0</c>.</summary>
public static class WeavePackageVersion
{
    /// <summary>Negative when <paramref name="a"/> is older than <paramref name="b"/>, zero when equal, positive when newer.</summary>
    public static int Compare(string a, string b)
    {
        var (coreA, preA) = Split(a);
        var (coreB, preB) = Split(b);
        for (var i = 0; i < Math.Max(coreA.Length, coreB.Length); i++)
        {
            var difference = (i < coreA.Length ? coreA[i] : 0).CompareTo(i < coreB.Length ? coreB[i] : 0);
            if (difference != 0)
                return difference;
        }

        // A release is newer than its pre-releases.
        if (preA.Length == 0 || preB.Length == 0)
            return preB.Length.CompareTo(preA.Length) switch { > 0 => 1, < 0 => -1, _ => 0 };

        for (var i = 0; i < Math.Min(preA.Length, preB.Length); i++)
        {
            var numericA = int.TryParse(preA[i], out var numberA);
            var numericB = int.TryParse(preB[i], out var numberB);
            var difference = (numericA, numericB) switch
            {
                (true, true) => numberA.CompareTo(numberB),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(preA[i], preB[i]),
            };
            if (difference != 0)
                return difference;
        }

        return preA.Length.CompareTo(preB.Length);
    }

    /// <summary>The newer of the versions given, skipping missing ones.</summary>
    public static string? Newest(params string?[] versions)
        => versions.OfType<string>().Where(v => v.Length > 0).Aggregate((string?)null, (best, v) => best is null || Compare(v, best) > 0 ? v : best);

    private static (int[] Core, string[] PreRelease) Split(string version)
    {
        var withoutBuild = version.TrimStart('v', 'V').Split('+')[0];
        var dash = withoutBuild.IndexOf('-', StringComparison.Ordinal);
        var core = dash < 0 ? withoutBuild : withoutBuild[..dash];
        var pre = dash < 0 ? [] : withoutBuild[(dash + 1)..].Split('.');
        return ([.. core.Split('.').Select(part => int.TryParse(part, out var n) ? n : 0)], pre);
    }
}

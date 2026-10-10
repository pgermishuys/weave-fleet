using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace WeaveFleet.Application.Runtimes;

/// <summary>
/// A Bun version, parsed strictly as semver: <c>1.4.2</c>, or <c>1.4.3-canary.20+abc123</c>. A pre-release is older
/// than its release, so <c>1.4.3-canary.20</c> isn't safe when the oldest safe version is <c>1.4.3</c>. Build metadata
/// is ignored when comparing.
/// </summary>
public readonly partial record struct BunVersion(int Major, int Minor, int Patch, string? PreRelease) : IComparable<BunVersion>
{
    /// <summary>Parses <paramref name="text"/>, the whole of it: no <c>v</c> prefix, no spaces, no leading zeros.</summary>
    public static bool TryParse([NotNullWhen(true)] string? text, out BunVersion version)
    {
        version = default;
        if (text is null || text.Length > 128)
            return false;

        var match = Pattern().Match(text);
        if (!match.Success
            || !int.TryParse(match.Groups["major"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(match.Groups["minor"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || !int.TryParse(match.Groups["patch"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
            return false;

        version = new BunVersion(major, minor, patch, match.Groups["pre"].Success ? match.Groups["pre"].Value : null);
        return true;
    }

    /// <summary>Parses <paramref name="text"/> or throws <see cref="FormatException"/>.</summary>
    public static BunVersion Parse(string text) =>
        TryParse(text, out var version) ? version : throw new FormatException($"'{text}' isn't a Bun version.");

    /// <inheritdoc />
    public int CompareTo(BunVersion other)
    {
        var core = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
        if (core != 0)
            return core;

        return (PreRelease, other.PreRelease) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            var (left, right) => ComparePreRelease(left, right),
        };
    }

    /// <summary>True when this version is older than <paramref name="other"/>.</summary>
    public bool IsOlderThan(BunVersion other) => CompareTo(other) < 0;

    /// <summary>Older than.</summary>
    public static bool operator <(BunVersion left, BunVersion right) => left.CompareTo(right) < 0;

    /// <summary>Older than or the same as.</summary>
    public static bool operator <=(BunVersion left, BunVersion right) => left.CompareTo(right) <= 0;

    /// <summary>Newer than.</summary>
    public static bool operator >(BunVersion left, BunVersion right) => left.CompareTo(right) > 0;

    /// <summary>Newer than or the same as.</summary>
    public static bool operator >=(BunVersion left, BunVersion right) => left.CompareTo(right) >= 0;

    /// <inheritdoc />
    public override string ToString() =>
        PreRelease is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{PreRelease}";

    /// <summary>Semver §11: dot-separated identifiers, numbers compared as numbers and before words.</summary>
    private static int ComparePreRelease(string left, string right)
    {
        var a = left.Split('.');
        var b = right.Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var leftIsNumber = long.TryParse(a[i], NumberStyles.None, CultureInfo.InvariantCulture, out var leftNumber);
            var rightIsNumber = long.TryParse(b[i], NumberStyles.None, CultureInfo.InvariantCulture, out var rightNumber);
            var difference = (leftIsNumber, rightIsNumber) switch
            {
                (true, true) => leftNumber.CompareTo(rightNumber),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(a[i], b[i]),
            };
            if (difference != 0)
                return Math.Sign(difference);
        }

        return a.Length.CompareTo(b.Length);
    }

    [GeneratedRegex(
        @"^(?<major>0|[1-9][0-9]{0,8})\.(?<minor>0|[1-9][0-9]{0,8})\.(?<patch>0|[1-9][0-9]{0,8})" +
        @"(?:-(?<pre>(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*))?" +
        @"(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}

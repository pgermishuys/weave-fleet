namespace WeaveFleet.Application.Harnesses;

/// <summary>The user's per-harness switches, kept as <c>{type}.enabled</c> preferences.</summary>
public static class HarnessPreferences
{
    /// <summary>
    /// Whether <paramref name="harnessType"/> is on. Every harness is on until the user turns it off, so a computer with
    /// only Claude Code (or only Pi) can start sessions without a trip to Settings. One that isn't installed still can't.
    /// </summary>
    public static bool IsEnabled(string harnessType, IReadOnlyDictionary<string, string> preferenceValues)
        => !preferenceValues.TryGetValue($"{harnessType}.enabled", out var value)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
}

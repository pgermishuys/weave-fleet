namespace WeaveFleet.Application.Harnesses;

/// <summary>The user's per-harness switches, kept as <c>{type}.enabled</c> preferences, and their default harness.</summary>
public static class HarnessPreferences
{
    /// <summary>The preference that keeps the harness a session starts on when it names none (Settings → Harnesses → Set default).</summary>
    public const string DefaultHarnessKey = "defaultHarnessType";

    /// <summary>The default harness while the user hasn't picked one and Fleet knows of none that's ready.</summary>
    public const string FallbackDefaultHarness = "opencode2";

    /// <summary>
    /// Whether <paramref name="harnessType"/> is on. Every harness is on until the user turns it off, so a computer with
    /// only Claude Code (or only Pi) can start sessions without a trip to Settings. One that isn't installed still can't.
    /// </summary>
    public static bool IsEnabled(string harnessType, IReadOnlyDictionary<string, string> preferenceValues)
        => !preferenceValues.TryGetValue($"{harnessType}.enabled", out var value)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The harness a session that names none starts on: the user's pick; else the first of <paramref name="harnesses"/>
    /// (in Fleet's order, OpenCode 2 first) that's installed and on; else <see cref="FallbackDefaultHarness"/>. The
    /// new-session box picks the same way, so a computer with only OpenCode 1 or only Claude Code keeps working.
    /// </summary>
    /// <param name="harnesses">The harnesses as Fleet last checked them; null when it can't say.</param>
    public static string DefaultHarness(IReadOnlyDictionary<string, string> preferenceValues, IReadOnlyList<HarnessInfo>? harnesses)
    {
        if (preferenceValues.TryGetValue(DefaultHarnessKey, out var preferred) && !string.IsNullOrWhiteSpace(preferred))
            return preferred;

        return harnesses?.FirstOrDefault(harness => harness.Available && IsEnabled(harness.Type, preferenceValues))?.Type
            ?? FallbackDefaultHarness;
    }
}

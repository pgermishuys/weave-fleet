namespace WeaveFleet.Application.Harnesses;

/// <summary>The user's per-harness switches, kept as <c>{type}.enabled</c> preferences, and their default harness.</summary>
public static class HarnessPreferences
{
    /// <summary>The preference that keeps the harness a session starts on when it names none (Settings → Harnesses → Set default).</summary>
    public const string DefaultHarnessKey = "defaultHarnessType";

    /// <summary>The default harness while the user hasn't picked one.</summary>
    public const string FallbackDefaultHarness = "opencode";

    /// <summary>
    /// Whether <paramref name="harnessType"/> is on. Every harness is on until the user turns it off, so a computer with
    /// only Claude Code (or only Pi) can start sessions without a trip to Settings. One that isn't installed still can't.
    /// </summary>
    public static bool IsEnabled(string harnessType, IReadOnlyDictionary<string, string> preferenceValues)
        => !preferenceValues.TryGetValue($"{harnessType}.enabled", out var value)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>The harness a session starts on when it names none: the user's pick (<paramref name="preferred"/>), or the fallback.</summary>
    public static string DefaultHarness(string? preferred)
        => string.IsNullOrWhiteSpace(preferred) ? FallbackDefaultHarness : preferred;
}

namespace WeaveFleet.Application.Harnesses;

/// <summary>The user's per-harness switches, kept as <c>{type}.enabled</c> preferences.</summary>
public static class HarnessPreferences
{
    /// <summary>Whether the user turned <paramref name="harnessType"/> on. Only OpenCode is on until they choose.</summary>
    public static bool IsEnabled(string harnessType, IReadOnlyDictionary<string, string> preferenceValues)
    {
        if (preferenceValues.TryGetValue($"{harnessType}.enabled", out var value))
            return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

        return string.Equals(harnessType, "opencode", StringComparison.OrdinalIgnoreCase);
    }
}

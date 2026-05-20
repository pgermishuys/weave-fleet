using Xunit.Abstractions;

namespace WeaveFleet.E2E.Infrastructure;

/// <summary>
/// Describes one real harness runtime configuration for the opt-in harness smoke theory.
/// </summary>
public sealed record class HarnessSmokeSpec : IXunitSerializable
{
    private string[] _disabledHarnessPreferenceKeys = [];

    public HarnessSmokeSpec() { }

    public HarnessSmokeSpec(
        string harnessType,
        string enabledPreferenceKey,
        string displayName,
        IReadOnlyCollection<string> disabledHarnessPreferenceKeys)
    {
        HarnessType = RequireValue(harnessType, nameof(harnessType));
        EnabledPreferenceKey = RequireValue(enabledPreferenceKey, nameof(enabledPreferenceKey));
        DisplayName = RequireValue(displayName, nameof(displayName));
        _disabledHarnessPreferenceKeys = disabledHarnessPreferenceKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public string HarnessType { get; private set; } = string.Empty;

    public string EnabledPreferenceKey { get; private set; } = string.Empty;

    public string DisplayName { get; private set; } = string.Empty;

    public IReadOnlyList<string> DisabledHarnessPreferenceKeys => _disabledHarnessPreferenceKeys;

    public void Deserialize(IXunitSerializationInfo info)
    {
        HarnessType = info.GetValue<string>(nameof(HarnessType));
        EnabledPreferenceKey = info.GetValue<string>(nameof(EnabledPreferenceKey));
        DisplayName = info.GetValue<string>(nameof(DisplayName));

        var disabledKeys = info.GetValue<string>(nameof(DisabledHarnessPreferenceKeys));
        _disabledHarnessPreferenceKeys = string.IsNullOrWhiteSpace(disabledKeys)
            ? []
            : disabledKeys.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public void Serialize(IXunitSerializationInfo info)
    {
        info.AddValue(nameof(HarnessType), HarnessType);
        info.AddValue(nameof(EnabledPreferenceKey), EnabledPreferenceKey);
        info.AddValue(nameof(DisplayName), DisplayName);
        info.AddValue(nameof(DisabledHarnessPreferenceKeys), string.Join('\n', _disabledHarnessPreferenceKeys));
    }

    public override string ToString() => DisplayName;

    private static string RequireValue(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Value cannot be null or whitespace.", parameterName);

        return value.Trim();
    }
}

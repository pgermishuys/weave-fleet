using Xunit.Abstractions;

namespace WeaveFleet.E2E.Infrastructure;

/// <summary>
/// Describes one real harness runtime configuration for the opt-in harness smoke theory.
/// </summary>
public sealed record class HarnessSmokeSpec : IXunitSerializable
{
    private static readonly string[] KnownHarnessTypes =
    [
        "opencode",
        "claude-code",
        "nucode"
    ];

    public HarnessSmokeSpec() { }

    public HarnessSmokeSpec(string harnessType)
    {
        HarnessType = RequireValue(harnessType, nameof(harnessType));
    }

    public string HarnessType { get; private set; } = string.Empty;

    public string EnabledPreferenceKey => ToPreferenceKey(HarnessType);

    public IReadOnlyList<string> DisabledHarnessPreferenceKeys => KnownHarnessTypes
        .Where(type => !string.Equals(type, HarnessType, StringComparison.OrdinalIgnoreCase))
        .Select(ToPreferenceKey)
        .ToArray();

    public void Deserialize(IXunitSerializationInfo info)
    {
        HarnessType = info.GetValue<string>(nameof(HarnessType));
    }

    public void Serialize(IXunitSerializationInfo info)
    {
        info.AddValue(nameof(HarnessType), HarnessType);
    }

    public override string ToString() => HarnessType;

    private static string ToPreferenceKey(string harnessType) => $"{harnessType}.enabled";

    private static string RequireValue(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Value cannot be null or whitespace.", parameterName);

        return value.Trim();
    }
}

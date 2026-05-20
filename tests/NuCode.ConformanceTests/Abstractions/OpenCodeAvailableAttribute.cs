using NuCode.ConformanceTests.OpenCode;

namespace NuCode.ConformanceTests.Abstractions;

/// <summary>
/// Skips the test class when the <c>opencode</c> binary is not available on PATH.
/// Apply to any test class that requires a running opencode process.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class OpenCodeAvailableAttribute : Attribute
{
    /// <summary>Returns true if opencode is available on PATH.</summary>
    public static bool IsAvailable() => OpenCodeFixture.IsAvailable();

    /// <summary>Skip reason when opencode is not available.</summary>
    public const string SkipReason =
        "opencode binary not found on PATH. Install opencode to run these tests.";
}

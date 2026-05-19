using NuCode.ConformanceTests.Abstractions;

namespace NuCode.ConformanceTests.OpenCode;

/// <summary>
/// Runs all shared <see cref="HarnessConformanceBase"/> tests against <see cref="OpenCodeFixture"/>.
/// Skipped automatically when the <c>opencode</c> binary is not on PATH.
/// </summary>
public sealed class OpenCodeConformanceTests : HarnessConformanceBase
{
    public override async ValueTask InitializeAsync()
    {
        if (!OpenCodeAvailableAttribute.IsAvailable())
            throw new InvalidOperationException(
                Xunit.v3.DynamicSkipToken.Value + OpenCodeAvailableAttribute.SkipReason);

        await base.InitializeAsync();
    }

    protected override IHarnessSessionFixture CreateFixture() => new OpenCodeFixture();
}

using NuCode.ConformanceTests.Abstractions;

namespace NuCode.ConformanceTests.NuCode;

/// <summary>
/// Runs all shared <see cref="HarnessConformanceBase"/> tests against <see cref="NuCodeFixture"/>.
/// </summary>
public sealed class NuCodeConformanceTests : HarnessConformanceBase
{
    protected override IHarnessSessionFixture CreateFixture() => new NuCodeFixture();
}

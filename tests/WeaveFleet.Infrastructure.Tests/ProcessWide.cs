namespace WeaveFleet.Infrastructure.Tests;

/// <summary>
/// Tests that touch state the whole test process shares run one at a time, after the parallel tests: they set
/// environment variables (which every process a test starts inherits), or listen to Fleet's one static meter (which
/// records every test's measurements, not just their own).
/// </summary>
[CollectionDefinition(Collection, DisableParallelization = true)]
public sealed class ProcessWide
{
    public const string Collection = "Process-wide state";
}

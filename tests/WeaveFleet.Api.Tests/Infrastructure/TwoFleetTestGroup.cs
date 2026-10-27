namespace WeaveFleet.Api.Tests.Infrastructure;

/// <summary>
/// Tests that boot two Fleets calling each other. They run on their own, after the parallel tests: on a busy CPU a
/// call from one Fleet to the other can time out.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TwoFleetTestGroup
{
    public const string Name = "TwoFleetTests";
}

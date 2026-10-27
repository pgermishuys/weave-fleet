namespace WeaveFleet.Api.Tests.Infrastructure;

/// <summary>
/// Tests that boot two Fleets calling each other. They run on their own, after the parallel tests: on a busy CPU a
/// fresh Fleet answers its first call slowly (see <see cref="ApiWebApplicationFactory.RemoteMachinesTimeout"/>).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TwoFleetTestGroup
{
    public const string Name = "TwoFleetTests";
}

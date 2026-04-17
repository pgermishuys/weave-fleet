using WeaveFleet.Application.Configuration;

namespace WeaveFleet.Application.Tests.Configuration;

public sealed class NatsOptionsTests
{
    [Fact]
    public void Defaults_matchLocalDevMode()
    {
        var options = new NatsOptions();

        options.ExternalUrl.ShouldBeNull();
        options.CredsFile.ShouldBeNull();
        options.DataDirectory.ShouldBe("./data/nats");
        options.StreamName.ShouldBe("fleet-sessions");
        options.MaxAge.ShouldBe(TimeSpan.FromHours(24));
        options.MaxBytes.ShouldBe(-1);
        options.MaxPayloadBytes.ShouldBe(4 * 1024 * 1024);
        options.TenantPrefix.ShouldBe("tenant.default");
        options.NodeId.ShouldNotBeNullOrWhiteSpace();
        options.ProjectionRetryBudget.ShouldBe(5);
    }

    [Fact]
    public void FleetOptions_exposesNatsSection()
    {
        var options = new FleetOptions();

        options.Nats.ShouldNotBeNull();
        options.Nats.StreamName.ShouldBe("fleet-sessions");
    }
}

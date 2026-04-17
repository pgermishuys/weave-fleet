using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.JetStream;
using WeaveFleet.Application.Events;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Api.Tests.Nats;

/// <summary>
/// ApiWebApplicationFactory is sealed, so subclass WebApplicationFactory&lt;Program&gt; directly.
/// Overrides config so tests get an isolated data directory per factory instance.
/// </summary>
public sealed class NatsEnabledFactory : WebApplicationFactory<Program>
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"fleet-nats-e2e-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_tempDir);
        builder.UseSetting("Fleet:Nats:DataDirectory", Path.Combine(_tempDir, "nats"));
        builder.UseSetting("Fleet:Nats:StreamName", $"fleet-sessions-e2e-{Guid.NewGuid():N}".Substring(0, 24));
        builder.UseSetting("Fleet:Nats:TenantPrefix", $"tenant.e2e-{Guid.NewGuid().ToString("N").Substring(0, 8)}");
        builder.UseSetting("Fleet:DatabasePath", Path.Combine(_tempDir, "fleet.db"));
        builder.UseSetting("Fleet:AnalyticsDatabasePath", Path.Combine(_tempDir, "analytics.db"));
        builder.UseSetting("Fleet:AnalyticsEnabled", "false");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }
}

public sealed class NatsEventSubstrateEndToEndTests : IClassFixture<NatsEnabledFactory>
{
    private readonly NatsEnabledFactory _factory;
    public NatsEventSubstrateEndToEndTests(NatsEnabledFactory factory) => _factory = factory;

    [Fact]
    public async Task PublishDurableEvent_landsOnStream()
    {
        using var client = _factory.CreateClient(); // forces host startup
        using var scope = _factory.Services.CreateScope();

        var publisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();
        var evt = new HarnessEvent
        {
            Type = EventTypes.MessageCreated,
            SessionId = "sess-e2e",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new { info = new { role = "assistant" } })
        };
        await publisher.PublishAsync(
            evt,
            new EventPublishContext("sess-e2e", "proj-e2e", "user-e2e", "opencode", Sequence: 1),
            CancellationToken.None);

        var js = scope.ServiceProvider.GetRequiredService<INatsJSContext>();
        var opts = scope.ServiceProvider.GetRequiredService<WeaveFleet.Application.Configuration.NatsOptions>();
        var stream = await js.GetStreamAsync(opts.StreamName);
        stream.Info.State.Messages.ShouldBeGreaterThanOrEqualTo(1L);
    }
}

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Projections;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Nats;
using WeaveFleet.Infrastructure.Nats.Configuration;

namespace WeaveFleet.Infrastructure.Tests.Nats;

public sealed class ProjectionListenerTests : IClassFixture<EmbeddedNatsTestFixture>
{
    private readonly EmbeddedNatsTestFixture _fixture;
    public ProjectionListenerTests(EmbeddedNatsTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task DeliversEvent_parsesSubject_andAcks()
    {
        var slug = Guid.NewGuid().ToString("N").Substring(0, 8);
        var tenantPrefix = $"tenant.proj-{slug}";
        var streamFilter = $"{tenantPrefix}.project.*.session.*.>";
        var options = new NatsOptions
        {
            StreamName = $"fleet-proj-test-{slug}",
            TenantPrefix = tenantPrefix,
        };
        await using var conn = new NatsConnection(new NatsOpts { Url = _fixture.Url });
        var js = new NatsJSContext(conn);
        try { await js.DeleteStreamAsync(options.StreamName); } catch { }
        await js.CreateStreamAsync(new StreamConfig(options.StreamName, [streamFilter]));

        // Publish a synthetic event
        var evt = new HarnessEvent
        {
            Type = EventTypes.MessageCreated,
            SessionId = "sess-x",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new { info = new { role = "assistant" } }),
        };
        var headers = new NatsHeaders { ["x-fleet-user-id"] = "user-1", ["x-fleet-harness-type"] = "opencode" };
        await js.PublishAsync(
            subject: $"{tenantPrefix}.project.proj-1.session.sess-x.message.created",
            data: JsonSerializer.SerializeToUtf8Bytes(evt),
            headers: headers,
            opts: new NatsJSPubOpts { MsgId = "m1" });

        // Set up listener
        var received = new TaskCompletionSource<(HarnessEvent, ProjectionContext)>();
        var projection = new RecordingProjection(received);
        var services = new ServiceCollection();
        services.AddScoped<RecordingProjection>(_ => projection);
        services.AddLogging();
        var sp = services.BuildServiceProvider();

        var naming = new NatsNamingStrategy(options, nodeId: "test-node");
        // Pre-create the consumer — in production this is done by NatsStreamInitializer.
        await js.CreateOrUpdateConsumerAsync(options.StreamName, new ConsumerConfig(naming.ClusterConsumerName("recording"))
        {
            AckPolicy = ConsumerConfigAckPolicy.Explicit,
            DeliverPolicy = ConsumerConfigDeliverPolicy.All,
            FilterSubject = streamFilter,
            MaxDeliver = options.ProjectionRetryBudget,
        });

        var entry = new ProjectionRegistryEntry(typeof(RecordingProjection), ConsumerScope.Cluster);
        var listener = new ProjectionListener(
            entry, js, sp, naming, options, new NatsMetrics(),
            NullLogger<ProjectionListener>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var runTask = Task.Run(() => listener.RunAsync(cts.Token), cts.Token);

        var (receivedEvt, receivedCtx) = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        receivedEvt.Type.ShouldBe(EventTypes.MessageCreated);
        receivedCtx.ProjectId.ShouldBe("proj-1");
        receivedCtx.FleetSessionId.ShouldBe("sess-x");
        receivedCtx.UserId.ShouldBe("user-1");
        receivedCtx.HarnessType.ShouldBe("opencode");

        cts.Cancel();
        try { await runTask; } catch { }
    }

    private sealed class RecordingProjection : IProjection<HarnessEvent>
    {
        private readonly TaskCompletionSource<(HarnessEvent, ProjectionContext)> _tcs;
        public RecordingProjection(TaskCompletionSource<(HarnessEvent, ProjectionContext)> tcs) => _tcs = tcs;
        public string Name => "recording";
        public Task HandleAsync(HarnessEvent evt, ProjectionContext ctx, CancellationToken ct)
        {
            _tcs.TrySetResult((evt, ctx));
            return Task.CompletedTask;
        }
    }
}

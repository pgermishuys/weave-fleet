using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Nats;

namespace WeaveFleet.Infrastructure.Tests.Nats;

public sealed class NatsEventPublisherTests : IClassFixture<EmbeddedNatsTestFixture>
{
    private readonly EmbeddedNatsTestFixture _fixture;

    public NatsEventPublisherTests(EmbeddedNatsTestFixture fixture) => _fixture = fixture;

    private async Task<(NatsConnection Conn, NatsJSContext Js, NatsEventPublisher Publisher, NatsOptions Options)> BuildAsync(string streamSuffix)
    {
        // Give each test its own tenant prefix so stream subject filters don't overlap when
        // multiple test classes share the same embedded nats-server fixture.
        var tenantPrefix = $"tenant.test-{streamSuffix}";
        var options = new NatsOptions
        {
            StreamName = $"fleet-sessions-{streamSuffix}",
            TenantPrefix = tenantPrefix,
        };
        var conn = new NatsConnection(new NatsOpts { Url = _fixture.Url });
        var js = new NatsJSContext(conn);
        try { await js.DeleteStreamAsync(options.StreamName); } catch { }
        await js.CreateStreamAsync(new StreamConfig(options.StreamName, [$"{tenantPrefix}.project.*.session.*.>"]));
        var publisher = new NatsEventPublisher(
            new Lazy<INatsJSContext>(() => js),
            new Lazy<INatsConnection>(() => conn),
            new NatsNamingStrategy(options, nodeId: "test-node"),
            new NatsMetrics(),
            options,
            NullLogger<NatsEventPublisher>.Instance);
        return (conn, js, publisher, options);
    }

    [Fact]
    public async Task DurableEvent_publishesToJetStreamWithMsgIdHeader()
    {
        var (conn, js, publisher, options) = await BuildAsync("durable");
        await using var _conn = conn;

        var evt = new HarnessEvent
        {
            Type = EventTypes.MessageCreated,
            SessionId = "sess-1",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new { info = new { role = "assistant" } })
        };

        await publisher.PublishAsync(
            evt,
            new EventPublishContext("sess-1", ProjectId: "proj-1", UserId: "user-1", HarnessType: "opencode", Sequence: 42),
            CancellationToken.None);

        var stream = await js.GetStreamAsync(options.StreamName);
        stream.Info.State.Messages.ShouldBe(1L);

        // Verify message id header is present and in {sessionId}:{seq} format.
        var consumer = await js.CreateOrderedConsumerAsync(options.StreamName);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        bool verified = false;
        await foreach (var msg in consumer.ConsumeAsync<byte[]>(cancellationToken: cts.Token))
        {
            msg.Headers.ShouldNotBeNull();
            msg.Headers!["Nats-Msg-Id"].ToString().ShouldBe("sess-1:42");
            msg.Headers["x-fleet-user-id"].ToString().ShouldBe("user-1");
            msg.Headers["x-fleet-harness-type"].ToString().ShouldBe("opencode");
            msg.Subject.ShouldBe("tenant.test-durable.project.proj-1.session.sess-1.message.created");
            verified = true;
            break;
        }
        verified.ShouldBeTrue();
    }

    [Fact]
    public async Task EphemeralEvent_publishesToCoreNats()
    {
        var (conn, _, publisher, _) = await BuildAsync("ephemeral");
        await using var _conn = conn;

        // Start a subscription before publishing
        using var subCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var subTask = Task.Run(async () =>
        {
            await foreach (var msg in conn.SubscribeAsync<byte[]>("tenant.test-ephemeral.project.proj-1.live.sess-1.>", cancellationToken: subCts.Token))
            {
                return msg;
            }
            throw new InvalidOperationException("no message received");
        }, subCts.Token);

        // Allow subscription to register
        await Task.Delay(200);

        var evt = new HarnessEvent
        {
            Type = EventTypes.MessagePartDelta,
            SessionId = "sess-1",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new { delta = "hi" })
        };
        await publisher.PublishAsync(
            evt,
            new EventPublishContext("sess-1", "proj-1", "user-1", "opencode", Sequence: 1),
            CancellationToken.None);

        var received = await subTask.WaitAsync(TimeSpan.FromSeconds(5));
        received.Subject.ShouldBe("tenant.test-ephemeral.project.proj-1.live.sess-1.message.part.delta");
    }

    [Fact]
    public async Task UnknownClassification_isDropped()
    {
        var (conn, js, publisher, options) = await BuildAsync("unknown");
        await using var _conn = conn;

        var evt = new HarnessEvent
        {
            Type = "totally.unknown.type",
            SessionId = "sess-1",
            Timestamp = DateTimeOffset.UtcNow
        };
        await publisher.PublishAsync(
            evt,
            new EventPublishContext("sess-1", "proj-1", "user-1", "opencode", Sequence: 1),
            CancellationToken.None);

        var stream = await js.GetStreamAsync(options.StreamName);
        stream.Info.State.Messages.ShouldBe(0L);
    }

    [Fact]
    public async Task Publish_rejectsSubjectInjectionInIds()
    {
        var (conn, _, publisher, _) = await BuildAsync("inject");
        await using var _conn = conn;

        var evt = new HarnessEvent { Type = EventTypes.MessageCreated, SessionId = "sess-1", Timestamp = DateTimeOffset.UtcNow };

        await Should.ThrowAsync<ArgumentException>(() => publisher.PublishAsync(evt,
            new EventPublishContext("sess-1", "proj.sneaky", "user-1", "opencode", Sequence: 1), CancellationToken.None));

        await Should.ThrowAsync<ArgumentException>(() => publisher.PublishAsync(evt,
            new EventPublishContext("sess>inject", "proj-1", "user-1", "opencode", Sequence: 1), CancellationToken.None));
    }
}

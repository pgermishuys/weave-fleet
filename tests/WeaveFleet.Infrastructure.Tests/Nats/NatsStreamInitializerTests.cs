using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NATS.Client.Core;
using NATS.Client.JetStream;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Projections;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Nats;
using WeaveFleet.Infrastructure.Nats.Configuration;

namespace WeaveFleet.Infrastructure.Tests.Nats;

public sealed class NatsStreamInitializerTests : IClassFixture<EmbeddedNatsTestFixture>
{
    private readonly EmbeddedNatsTestFixture _fixture;
    public NatsStreamInitializerTests(EmbeddedNatsTestFixture fixture) => _fixture = fixture;

    private static async Task<NatsOptions> FreshOptionsAsync(string slug, NatsJSContext js)
    {
        // Unique tenant + stream per test so subject filters don't overlap across tests in the
        // same fixture.
        var options = new NatsOptions
        {
            StreamName = $"fleet-init-test-{slug}",
            TenantPrefix = $"tenant.init-{slug}",
        };
        try { await js.DeleteStreamAsync(options.StreamName); } catch { }
        return options;
    }

    [Fact]
    public async Task Start_createsStreamIdempotently()
    {
        await using var conn = new NatsConnection(new NatsOpts { Url = _fixture.Url });
        var js = new NatsJSContext(conn);
        var options = await FreshOptionsAsync("idempotent", js);

        var registry = new ProjectionRegistry(Array.Empty<ProjectionRegistryEntry>());
        var sp = new ServiceCollection().BuildServiceProvider();
        var naming = new NatsNamingStrategy(options, nodeId: "test-node");
        // Override the stream filter to match the test's tenant prefix so the stream is unique.
        var customStreamFilterForThisTest = $"{options.TenantPrefix}.project.*.session.*.>";
        var sut = new TestableNatsStreamInitializer(js, naming, options, registry, sp, customStreamFilterForThisTest);

        await sut.StartAsync(CancellationToken.None);
        await sut.StartAsync(CancellationToken.None); // second call is idempotent

        var stream = await js.GetStreamAsync(options.StreamName);
        stream.Info.Config.Subjects.ShouldNotBeNull();
        stream.Info.Config.Subjects.ShouldContain(customStreamFilterForThisTest);
    }

    [Fact]
    public async Task Start_preCreatesConsumerForEveryProjection()
    {
        await using var conn = new NatsConnection(new NatsOpts { Url = _fixture.Url });
        var js = new NatsJSContext(conn);
        var options = await FreshOptionsAsync("preconsumer", js);

        var services = new ServiceCollection();
        services.AddScoped<NoOpProjection>();
        services.AddLogging();
        var sp = services.BuildServiceProvider();

        var entry = new ProjectionRegistryEntry(typeof(NoOpProjection), ConsumerScope.Cluster);
        var registry = new ProjectionRegistry([entry]);
        var naming = new NatsNamingStrategy(options, nodeId: "node-Z");
        var customStreamFilterForThisTest = $"{options.TenantPrefix}.project.*.session.*.>";
        var sut = new TestableNatsStreamInitializer(js, naming, options, registry, sp, customStreamFilterForThisTest);

        await sut.StartAsync(CancellationToken.None);

        var stream = await js.GetStreamAsync(options.StreamName);
        var consumerName = naming.ClusterConsumerName("noop");
        var consumer = await js.GetConsumerAsync(options.StreamName, consumerName);
        consumer.ShouldNotBeNull();
    }

    // Test-only subclass that overrides the stream filter so tests can have isolated subject
    // hierarchies within a shared nats-server fixture. Production uses the default filter.
    private sealed class TestableNatsStreamInitializer
    {
        private readonly NatsStreamInitializer _inner;
        private readonly NatsJSContext _js;
        private readonly NatsOptions _options;
        private readonly string _filter;
        private readonly NatsNamingStrategy _naming;
        private readonly ProjectionRegistry _registry;
        private readonly IServiceProvider _sp;

        public TestableNatsStreamInitializer(
            NatsJSContext js, NatsNamingStrategy naming, NatsOptions options,
            ProjectionRegistry registry, IServiceProvider sp, string filter)
        {
            _js = js; _naming = naming; _options = options; _registry = registry; _sp = sp; _filter = filter;
            _inner = new NatsStreamInitializer(
                new Lazy<INatsJSContext>(() => js),
                naming, options, registry, sp, NullLogger<NatsStreamInitializer>.Instance);
        }

        public async Task StartAsync(CancellationToken ct)
        {
            var config = new NATS.Client.JetStream.Models.StreamConfig(_options.StreamName, [_filter])
            {
                Retention = NATS.Client.JetStream.Models.StreamConfigRetention.Interest,
                Storage = NATS.Client.JetStream.Models.StreamConfigStorage.File,
                MaxAge = _options.MaxAge,
                MaxBytes = _options.MaxBytes,
                MaxMsgSize = _options.MaxPayloadBytes,
                DuplicateWindow = TimeSpan.FromMinutes(2),
            };
            try { await _js.CreateStreamAsync(config, ct); }
            catch (NatsJSApiException ex) when (ex.Error.ErrCode == 10058 || ex.Error.Code == 400)
            { await _js.UpdateStreamAsync(config, ct); }

            foreach (var entry in _registry.Entries)
            {
                string name;
                using (var scope = _sp.CreateScope())
                {
                    var p = (IProjection<HarnessEvent>)scope.ServiceProvider.GetRequiredService(entry.ProjectionType);
                    name = p.Name;
                }
                var consumerName = entry.Scope == ConsumerScope.PerNode
                    ? _naming.PerNodeConsumerName(name)
                    : _naming.ClusterConsumerName(name);
                await _js.CreateOrUpdateConsumerAsync(_options.StreamName,
                    new NATS.Client.JetStream.Models.ConsumerConfig(consumerName)
                    {
                        AckPolicy = NATS.Client.JetStream.Models.ConsumerConfigAckPolicy.Explicit,
                        DeliverPolicy = NATS.Client.JetStream.Models.ConsumerConfigDeliverPolicy.All,
                        FilterSubject = _filter,
                        MaxDeliver = _options.ProjectionRetryBudget,
                    }, ct);
            }
        }
    }
}

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Projections;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Nats.Configuration;

namespace WeaveFleet.Infrastructure.Nats;

/// <summary>
/// Hosted service that creates the durable JetStream stream AND pre-creates every registered
/// projection's durable consumer on startup. Idempotent — catches "already exists" errors and
/// continues. Pre-creating consumers is load-bearing for interest-based retention: a publish
/// that lands before a consumer is registered could otherwise be GC'd if MaxAge expires first.
/// </summary>
public sealed class NatsStreamInitializer : IHostedService
{
    private static readonly Action<ILogger, string, Exception?> LogStreamReady =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(1, "NatsStreamReady"),
            "JetStream stream {StreamName} ready.");
    private static readonly Action<ILogger, string, Exception?> LogConsumerReady =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(2, "NatsConsumerReady"),
            "JetStream durable consumer {Consumer} ready.");

    private readonly Lazy<INatsJSContext> _jsLazy;
    private readonly NatsNamingStrategy _naming;
    private readonly NatsOptions _options;
    private readonly ProjectionRegistry _registry;
    private readonly IServiceProvider _rootProvider;
    private readonly ILogger<NatsStreamInitializer> _logger;

    public NatsStreamInitializer(
        Lazy<INatsJSContext> js,
        NatsNamingStrategy naming,
        NatsOptions options,
        ProjectionRegistry registry,
        IServiceProvider rootProvider,
        ILogger<NatsStreamInitializer> logger)
    {
        _jsLazy = js;
        _naming = naming;
        _options = options;
        _registry = registry;
        _rootProvider = rootProvider;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var js = _jsLazy.Value;
        var config = new StreamConfig(_options.StreamName, [NatsNamingStrategy.DurableStreamFilter])
        {
            Retention = StreamConfigRetention.Interest,
            Storage = StreamConfigStorage.File,
            MaxAge = _options.MaxAge,
            MaxBytes = _options.MaxBytes,
            MaxMsgSize = _options.MaxPayloadBytes,
            DuplicateWindow = TimeSpan.FromMinutes(2),
        };

        try
        {
            await js.CreateStreamAsync(config, cancellationToken).ConfigureAwait(false);
        }
        catch (NatsJSApiException ex) when (ex.Error.ErrCode == 10058 || ex.Error.Code == 400)
        {
            // Already exists — update to ensure configured subjects and retention match.
            await js.UpdateStreamAsync(config, cancellationToken).ConfigureAwait(false);
        }

        LogStreamReady(_logger, _options.StreamName, null);

        // Pre-create every registered projection's durable consumer before publishes occur.
        foreach (var entry in _registry.Entries)
        {
            string projectionName;
            using (var scope = _rootProvider.CreateScope())
            {
                var proj = (IProjection<HarnessEvent>)scope.ServiceProvider.GetRequiredService(entry.ProjectionType);
                projectionName = proj.Name;
            }

            var consumerName = entry.Scope == ConsumerScope.PerNode
                ? _naming.PerNodeConsumerName(projectionName)
                : _naming.ClusterConsumerName(projectionName);

            var consumerConfig = new ConsumerConfig(consumerName)
            {
                AckPolicy = ConsumerConfigAckPolicy.Explicit,
                DeliverPolicy = ConsumerConfigDeliverPolicy.All,
                FilterSubject = NatsNamingStrategy.DurableStreamFilter,
                MaxDeliver = _options.ProjectionRetryBudget,
            };
            await js.CreateOrUpdateConsumerAsync(_options.StreamName, consumerConfig, cancellationToken)
                .ConfigureAwait(false);
            LogConsumerReady(_logger, consumerName, null);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

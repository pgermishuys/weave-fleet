using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;
using NATS.Client.JetStream;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Events;

namespace WeaveFleet.Infrastructure.Nats.Configuration;

public static class NatsServiceCollectionExtensions
{
    /// <summary>
    /// Register the NATS event substrate: embedded server (if applicable), stream initializer,
    /// publisher, projection host, and any projections declared via the fluent builder.
    /// <para>
    /// Does NOT register the ephemeral-event relay service — that is wired in
    /// <c>DependencyInjection.cs</c> alongside its broadcaster dependency so registration order
    /// is always correct.
    /// </para>
    /// </summary>
    public static IServiceCollection AddEventStore(
        this IServiceCollection services,
        NatsOptions options,
        Action<NatsStreamBuilder> configure)
    {
        services.AddSingleton(options);
        services.AddSingleton<NatsMetrics>();
        services.AddSingleton(sp => new NatsNamingStrategy(options, options.NodeId));
        services.AddSingleton<NatsServerHostedService>();
        services.AddHostedService(sp => sp.GetRequiredService<NatsServerHostedService>());

        // NATS connection — registered as a Lazy so DI can resolve it eagerly (for hosted-service
        // construction) without actually building the connection until the first time it is
        // accessed. By that time, NatsServerHostedService.StartAsync has run and ResolvedUrl is
        // populated. Hosted services start in registration order, so the embedded server is up
        // before any dependent hosted service (stream initializer, projection host) reaches
        // its StartAsync where the connection is first used.
        services.AddSingleton(sp => new Lazy<INatsConnection>(() =>
        {
            var server = sp.GetRequiredService<NatsServerHostedService>();
            if (string.IsNullOrWhiteSpace(server.ResolvedUrl))
                throw new InvalidOperationException(
                    "NatsServerHostedService must start before the NATS connection is requested.");
            var authOpts = options.CredsFile is { Length: > 0 }
                ? NatsAuthOpts.Default with { CredsFile = options.CredsFile }
                : NatsAuthOpts.Default;
            var natsOpts = new NatsOpts
            {
                Url = server.ResolvedUrl,
                AuthOpts = authOpts,
            };
            return new NatsConnection(natsOpts);
        }));
        services.AddSingleton<INatsConnection>(sp => sp.GetRequiredService<Lazy<INatsConnection>>().Value);
        services.AddSingleton(sp => new Lazy<INatsJSContext>(() =>
            new NatsJSContext((NatsConnection)sp.GetRequiredService<Lazy<INatsConnection>>().Value)));
        services.AddSingleton<INatsJSContext>(sp => sp.GetRequiredService<Lazy<INatsJSContext>>().Value);

        var builder = new NatsStreamBuilder(services);
        configure(builder);
        services.AddSingleton(new ProjectionRegistry(builder.Entries));

        // Order matters: stream + consumers must be ready before anything publishes.
        services.AddHostedService<NatsStreamInitializer>();

        services.AddSingleton<IEventPublisher, NatsEventPublisher>();

        services.AddHostedService<ProjectionHostService>();

        return services;
    }
}

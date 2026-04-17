using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NATS.Client.JetStream;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Infrastructure.Nats.Configuration;

namespace WeaveFleet.Infrastructure.Nats;

/// <summary>
/// Hosted service that spins up one <see cref="ProjectionListener"/> per registered projection.
/// </summary>
public sealed class ProjectionHostService : BackgroundService
{
    private readonly ProjectionRegistry _registry;
    private readonly Lazy<INatsJSContext> _jsLazy;
    private readonly IServiceProvider _rootProvider;
    private readonly NatsNamingStrategy _naming;
    private readonly NatsOptions _options;
    private readonly NatsMetrics _metrics;
    private readonly ILoggerFactory _loggerFactory;

    public ProjectionHostService(
        ProjectionRegistry registry,
        Lazy<INatsJSContext> js,
        IServiceProvider rootProvider,
        NatsNamingStrategy naming,
        NatsOptions options,
        NatsMetrics metrics,
        ILoggerFactory loggerFactory)
    {
        _registry = registry;
        _jsLazy = js;
        _rootProvider = rootProvider;
        _naming = naming;
        _options = options;
        _metrics = metrics;
        _loggerFactory = loggerFactory;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_registry.Entries.Count == 0) return Task.CompletedTask;

        var js = _jsLazy.Value;
        var tasks = _registry.Entries.Select(entry =>
        {
            var listener = new ProjectionListener(
                entry, js, _rootProvider, _naming, _options, _metrics,
                _loggerFactory.CreateLogger<ProjectionListener>());
            return Task.Run(() => listener.RunAsync(stoppingToken), stoppingToken);
        }).ToArray();
        return Task.WhenAll(tasks);
    }
}

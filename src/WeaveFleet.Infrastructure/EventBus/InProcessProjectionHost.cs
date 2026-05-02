using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Projections;
using WeaveFleet.Domain.Harnesses;
namespace WeaveFleet.Infrastructure.EventBus;

/// <summary>
/// Background service that dispatches durable events from the <c>inproc_events</c> store to
/// all registered <see cref="IProjection{HarnessEvent}"/> implementations.
/// <para>
/// On every wake-up (signalled by <see cref="InProcessChannels.ProjectionWakeUp"/>), queries
/// the store for all rows with <c>id &gt; lastProcessedId</c> and dispatches them in order.
/// On startup, <c>lastProcessedId = 0</c> so all historical (undispatched) rows are replayed.
/// </para>
/// <para>
/// Failure policy: one retry, then log and skip (mark dispatched). Since all dispatch happens
/// in-process, persistent failures indicate a projection bug rather than a transient fault.
/// </para>
/// </summary>
internal sealed partial class InProcessProjectionHost : BackgroundService
{
    private readonly InProcessEventStore _store;
    private readonly InProcessChannels _channels;
    private readonly ProjectionRegistry _registry;
    private readonly InProcessMetrics _metrics;
    private readonly IServiceProvider _rootProvider;
    private readonly ILogger<InProcessProjectionHost> _logger;

    public InProcessProjectionHost(
        InProcessEventStore store,
        InProcessChannels channels,
        ProjectionRegistry registry,
        InProcessMetrics metrics,
        IServiceProvider rootProvider,
        ILogger<InProcessProjectionHost> logger)
    {
        _store = store;
        _channels = channels;
        _registry = registry;
        _metrics = metrics;
        _rootProvider = rootProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        long lastId = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            var rows = _store.ReadPending(lastId);

            if (rows.Count == 0)
            {
                // Nothing pending — wait for the next wake-up signal.
                try
                {
                    await _channels.ProjectionWakeUp.Reader.ReadAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                continue;
            }

            foreach (var (id, envelope) in rows)
            {
                if (stoppingToken.IsCancellationRequested) break;
                await DispatchAsync(id, envelope, stoppingToken).ConfigureAwait(false);
                lastId = id;
            }
        }
    }

    private async Task DispatchAsync(long storeId, InProcessEnvelope envelope, CancellationToken ct)
    {
        foreach (var entry in _registry.Entries)
        {
            string projectionName = entry.ProjectionType.Name;
            var sw = Stopwatch.StartNew();
            string result = "ok";

            try
            {
                await InvokeProjectionAsync(entry, envelope, storeId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One retry.
                LogProjectionFailed(_logger, projectionName, ex);
                try
                {
                    await InvokeProjectionAsync(entry, envelope, storeId, ct).ConfigureAwait(false);
                }
                catch (Exception retryEx) when (retryEx is not OperationCanceledException)
                {
                    LogProjectionSkipped(_logger, projectionName, retryEx);
                    result = "skip";
                }
            }
            finally
            {
                sw.Stop();
                _metrics.RecordProjectionHandler(sw.Elapsed.TotalMilliseconds, projectionName, envelope.EventType, result);
                _metrics.RecordProjectionDispatch(projectionName, result);
            }
        }

        // Mark dispatched regardless of outcome so we don't replay indefinitely on a buggy projection.
        _store.MarkDispatched(storeId);
    }

    private async Task InvokeProjectionAsync(ProjectionRegistryEntry entry, InProcessEnvelope envelope, long storeId, CancellationToken ct)
    {
        using var scope = _rootProvider.CreateScope();
        var projection = (IProjection<HarnessEvent>)scope.ServiceProvider.GetRequiredService(entry.ProjectionType);

        var ctx = new ProjectionContext(
            Tenant:          envelope.Tenant,
            ProjectId:       envelope.ProjectId,
            FleetSessionId:  envelope.SessionId,
            EventType:       envelope.EventType,
            UserId:          envelope.UserId,
            HarnessType:     envelope.HarnessType,
            StreamSequence:  storeId,
            PublishSequence: envelope.Sequence);

        await projection.HandleAsync(envelope.Event, ctx, ct).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Error, EventId = 1,
        Message = "In-process projection {Projection} handler threw — retrying once.")]
    private static partial void LogProjectionFailed(ILogger logger, string projection, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, EventId = 2,
        Message = "In-process projection {Projection} failed after retry — skipping event.")]
    private static partial void LogProjectionSkipped(ILogger logger, string projection, Exception ex);
}

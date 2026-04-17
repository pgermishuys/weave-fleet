using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NATS.Client.JetStream;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Projections;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Nats.Configuration;

namespace WeaveFleet.Infrastructure.Nats;

/// <summary>
/// Pumps messages from a pre-created durable JetStream consumer into a single
/// <see cref="IProjection{HarnessEvent}"/>. Deserialization failures and malformed subjects are
/// TERM'd immediately. Handler exceptions are NAK'd up to
/// <see cref="NatsOptions.ProjectionRetryBudget"/>, after which the final attempt is TERM'd.
/// Payloads larger than <see cref="NatsOptions.MaxPayloadBytes"/> are TERM'd. OpenTelemetry
/// trace context is extracted from headers before invoking the handler.
/// </summary>
public sealed class ProjectionListener
{
    private static readonly ActivitySource ActivitySource = new("WeaveFleet.Nats.Consume");

    private static readonly Action<ILogger, string, Exception?> LogHandlerFailed =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(1, "ProjectionHandlerFailed"),
            "Projection {Projection} handler threw");
    private static readonly Action<ILogger, string, int, Exception?> LogPoisoned =
        LoggerMessage.Define<string, int>(LogLevel.Warning, new EventId(2, "ProjectionPoisonMessage"),
            "Projection {Projection} TERM'd message after {Attempts} failed delivery attempts");
    private static readonly Action<ILogger, string, int, Exception?> LogOversize =
        LoggerMessage.Define<string, int>(LogLevel.Warning, new EventId(3, "ProjectionOversizePayload"),
            "Projection {Projection} TERM'd an oversize payload of {Bytes} bytes");
    private static readonly Action<ILogger, string, Exception?> LogMalformed =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(4, "ProjectionMalformedPayload"),
            "Projection {Projection} TERM'd a malformed payload");

    private readonly ProjectionRegistryEntry _entry;
    private readonly INatsJSContext _js;
    private readonly IServiceProvider _rootProvider;
    private readonly NatsNamingStrategy _naming;
    private readonly NatsOptions _options;
    private readonly NatsMetrics _metrics;
    private readonly ILogger<ProjectionListener> _logger;

    public ProjectionListener(
        ProjectionRegistryEntry entry,
        INatsJSContext js,
        IServiceProvider rootProvider,
        NatsNamingStrategy naming,
        NatsOptions options,
        NatsMetrics metrics,
        ILogger<ProjectionListener> logger)
    {
        _entry = entry;
        _js = js;
        _rootProvider = rootProvider;
        _naming = naming;
        _options = options;
        _metrics = metrics;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        string projectionName;
        using (var scope = _rootProvider.CreateScope())
        {
            var temp = (IProjection<HarnessEvent>)scope.ServiceProvider.GetRequiredService(_entry.ProjectionType);
            projectionName = temp.Name;
        }

        var consumerName = _entry.Scope == ConsumerScope.PerNode
            ? _naming.PerNodeConsumerName(projectionName)
            : _naming.ClusterConsumerName(projectionName);

        // Consumer was pre-created by NatsStreamInitializer. Bind to it.
        var consumer = await _js.GetConsumerAsync(_naming.StreamName, consumerName, ct).ConfigureAwait(false);

        await foreach (var msg in consumer.ConsumeAsync<byte[]>(cancellationToken: ct).ConfigureAwait(false))
        {
            await HandleSingleAsync(msg, projectionName, ct).ConfigureAwait(false);
        }
    }

    private async Task HandleSingleAsync(INatsJSMsg<byte[]> msg, string projectionName, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        string result = "ack";

        // 1. Oversize guard.
        if (msg.Data is { Length: > 0 } data && data.Length > _options.MaxPayloadBytes)
        {
            LogOversize(_logger, projectionName, data.Length, null);
            try { await msg.AckTerminateAsync(cancellationToken: ct).ConfigureAwait(false); } catch { }
            _metrics.RecordProjectionAck(projectionName, "term");
            return;
        }

        // 2. Subject + payload parse — malformed input is immediate poison.
        NatsNamingStrategy.ParsedSubject parsed;
        HarnessEvent evt;
        try
        {
            parsed = NatsNamingStrategy.ParseSubject(msg.Subject)
                ?? throw new InvalidOperationException($"Subject did not parse: {msg.Subject}");
            evt = JsonSerializer.Deserialize<HarnessEvent>(msg.Data!)
                ?? throw new InvalidOperationException("Payload did not deserialize to HarnessEvent.");
        }
        catch (Exception ex)
        {
            LogMalformed(_logger, projectionName, ex);
            try { await msg.AckTerminateAsync(cancellationToken: ct).ConfigureAwait(false); } catch { }
            _metrics.RecordProjectionAck(projectionName, "term");
            return;
        }

        // 3. Extract OTel trace context from headers before invoking the handler.
        var parentContext = ExtractTraceContext(msg.Headers);
        using var activity = ActivitySource.StartActivity(
            $"nats.consume {projectionName}",
            ActivityKind.Consumer,
            parentContext);
        activity?.SetTag("session.id", parsed.SessionId);
        activity?.SetTag("project.id", parsed.ProjectId);
        activity?.SetTag("tenant.id", parsed.Tenant);
        activity?.SetTag("event.type", parsed.EventType);

        string? userId = msg.Headers is not null && msg.Headers.TryGetValue("x-fleet-user-id", out var userIdH)
            ? userIdH.ToString() : null;
        string? harnessType = msg.Headers is not null && msg.Headers.TryGetValue("x-fleet-harness-type", out var harnessH)
            ? harnessH.ToString() : null;

        var ctx = new ProjectionContext(
            Tenant: parsed.Tenant,
            ProjectId: parsed.ProjectId,
            FleetSessionId: parsed.SessionId,
            EventType: parsed.EventType,
            UserId: string.IsNullOrEmpty(userId) ? null : userId,
            HarnessType: string.IsNullOrEmpty(harnessType) ? null : harnessType,
            StreamSequence: (long)(msg.Metadata?.Sequence.Stream ?? 0));

        try
        {
            using var scope = _rootProvider.CreateScope();
            var projection = (IProjection<HarnessEvent>)scope.ServiceProvider.GetRequiredService(_entry.ProjectionType);
            await projection.HandleAsync(evt, ctx, ct).ConfigureAwait(false);
            await msg.AckAsync(cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogHandlerFailed(_logger, projectionName, ex);
            var numDelivered = (int)(msg.Metadata?.NumDelivered ?? 1);
            if (numDelivered >= _options.ProjectionRetryBudget)
            {
                LogPoisoned(_logger, projectionName, numDelivered, null);
                try { await msg.AckTerminateAsync(cancellationToken: ct).ConfigureAwait(false); } catch { }
                result = "term";
            }
            else
            {
                try { await msg.NakAsync(cancellationToken: ct).ConfigureAwait(false); } catch { }
                result = "nak";
            }
        }
        finally
        {
            sw.Stop();
            _metrics.RecordProjectionHandler(sw.Elapsed.TotalMilliseconds, projectionName, msg.Subject, result);
            _metrics.RecordProjectionAck(projectionName, result);
        }
    }

    private static ActivityContext ExtractTraceContext(NatsHeaders? headers)
    {
        if (headers is null) return default;
        DistributedContextPropagator.Current.ExtractTraceIdAndState(headers,
            static (object? carrier, string key, out string? value, out IEnumerable<string>? values) =>
            {
                values = null;
                if (carrier is NatsHeaders h && h.TryGetValue(key, out var raw))
                {
                    value = raw.ToString();
                    return;
                }
                value = null;
            },
            out var traceParent, out var traceState);
        return ActivityContext.TryParse(traceParent, traceState, out var parsed) ? parsed : default;
    }
}

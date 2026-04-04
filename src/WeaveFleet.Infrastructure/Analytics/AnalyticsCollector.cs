using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Analytics;

namespace WeaveFleet.Infrastructure.Analytics;

/// <summary>
/// Singleton analytics event collector. Accepts token and session events from the SSE pipeline
/// and buffers them in a bounded <see cref="Channel{T}"/> for batch processing by
/// <see cref="AnalyticsWriterService"/>. Never blocks; drops oldest events when full.
/// </summary>
public sealed partial class AnalyticsCollector : IAnalyticsCollector
{
    private readonly Channel<AnalyticsEventEnvelope> _channel;
    private readonly ILogger<AnalyticsCollector> _logger;

    public AnalyticsCollector(ILogger<AnalyticsCollector> logger)
    {
        _logger = logger;
        _channel = Channel.CreateBounded<AnalyticsEventEnvelope>(new BoundedChannelOptions(10_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
    }

    /// <summary>Exposes the channel reader for the writer service.</summary>
    public ChannelReader<AnalyticsEventEnvelope> Reader => _channel.Reader;

    /// <inheritdoc />
    public void AcceptTokenEvent(TokenEventData data)
    {
        if (!_channel.Writer.TryWrite(new TokenEventEnvelope(data)))
            LogChannelFull();
    }

    /// <inheritdoc />
    public void AcceptSessionSnapshot(SessionSnapshotData data)
    {
        if (!_channel.Writer.TryWrite(new SessionSnapshotEnvelope(data)))
            LogChannelFull();
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Analytics channel is full — oldest event dropped. Consider increasing AnalyticsMaxBatchSize or reducing AnalyticsFlushIntervalSeconds.")]
    private partial void LogChannelFull();
}

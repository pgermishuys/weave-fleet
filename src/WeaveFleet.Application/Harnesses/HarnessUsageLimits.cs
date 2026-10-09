using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Events;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Application.Harnesses;

/// <summary>A harness's usage limits as clients get them: the <c>harness.usage</c> event and <c>GET /api/harnesses/usage</c>.</summary>
/// <param name="HarnessType">The harness, e.g. <c>claude-code</c>.</param>
/// <param name="Windows">Every window it has reported, the latest of each.</param>
public sealed record HarnessUsage(string HarnessType, IReadOnlyList<UsageLimitWindow> Windows, DateTimeOffset UpdatedAt);

/// <summary>
/// The latest usage limits each harness reported, per user: they're the account's, not a session's, so each window's
/// latest report stands whichever session it came in. Kept in memory: a reload asks for them again, and after a restart
/// they come back with the harness's next report. Every change goes to the user's clients as <see cref="ChangedEvent"/>
/// on the <c>sessions</c> topic.
/// </summary>
public sealed partial class HarnessUsageLimits(IEventBroadcaster broadcaster, TimeProvider time, ILogger<HarnessUsageLimits> logger)
{
    /// <summary>The event carrying one harness's limits after they changed (<see cref="HarnessUsage"/>).</summary>
    public const string ChangedEvent = "harness.usage";

    private readonly ConcurrentDictionary<(string UserId, string HarnessType), HarnessUsage> _latest = new();
    private readonly Lock _sync = new();

    /// <summary>The limits the user's harnesses have reported, any windows that reset since left out.</summary>
    public IReadOnlyList<HarnessUsage> Get(string userId)
    {
        var now = time.GetUtcNow();
        return _latest
            .Where(entry => entry.Key.UserId == userId)
            .Select(entry => entry.Value with { Windows = [.. entry.Value.Windows.Where(window => window.ResetsAt is not { } reset || reset > now)] })
            .Where(usage => usage.Windows.Count > 0)
            .OrderBy(usage => usage.HarnessType, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>A harness's report: its windows replace what was known of them, and the user's clients are told.</summary>
    public async Task ObserveAsync(string? userId, string harnessType, UsageLimitReport report, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(userId) || report.Windows.Count == 0)
            return;

        HarnessUsage usage;
        lock (_sync)
        {
            var windows = _latest.TryGetValue((userId, harnessType), out var known)
                ? known.Windows.ToDictionary(window => window.Window, StringComparer.Ordinal)
                : new Dictionary<string, UsageLimitWindow>(StringComparer.Ordinal);
            var changed = false;
            foreach (var window in report.Windows)
            {
                if (windows.TryGetValue(window.Window, out var before) && before == window)
                    continue;
                windows[window.Window] = window;
                changed = true;
            }

            if (!changed)
                return;

            usage = new HarnessUsage(harnessType, [.. windows.Values.OrderBy(window => window.Window, StringComparer.Ordinal)], time.GetUtcNow());
            _latest[(userId, harnessType)] = usage;
        }

        try
        {
            var payload = JsonSerializer.SerializeToElement(usage, ApplicationJsonContext.Default.HarnessUsage);
            await broadcaster.BroadcastAsync("sessions", ChangedEvent, payload, userId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogBroadcastFailed(ex, harnessType);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't tell clients the usage limits of {HarnessType}")]
    private partial void LogBroadcastFailed(Exception ex, string harnessType);
}

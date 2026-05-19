namespace NuCode.ConformanceTests.Abstractions;

/// <summary>
/// Shared helper for collecting harness events until a predicate is satisfied or a timeout elapses.
/// </summary>
internal static class EventCollector
{
    /// <summary>Collects events from <see cref="IHarnessSession.SubscribeAsync"/> until <paramref name="until"/> returns <c>true</c> or timeout.</summary>
    public static async Task<List<HarnessEvent>> CollectAsync(
        IHarnessSession session,
        Func<List<HarnessEvent>, bool> until,
        TimeSpan? timeout = null)
    {
        var events = new List<HarnessEvent>();
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));

        try
        {
            await foreach (var evt in session.SubscribeAsync(cts.Token))
            {
                events.Add(evt);
                if (until(events))
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout — return what we have
        }

        return events;
    }
}

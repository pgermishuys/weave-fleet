namespace WeaveFleet.Application.Updates;

/// <summary>
/// When a prompt or an answer last reached this machine, from any device, another machine or the phone. A turn that
/// has not started yet still counts, so the automatic update doesn't restart into someone who has just sent a message.
/// </summary>
public sealed class UserActivityClock(TimeProvider time)
{
    private long _lastTicks;

    public DateTimeOffset? Last => Interlocked.Read(ref _lastTicks) is var t and > 0 ? new DateTimeOffset(t, TimeSpan.Zero) : null;

    public void Touch() => Interlocked.Exchange(ref _lastTicks, time.GetUtcNow().UtcTicks);
}

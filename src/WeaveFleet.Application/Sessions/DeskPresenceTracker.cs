using System.Collections.Concurrent;

namespace WeaveFleet.Application.Sessions;

/// <summary>
/// Which Fleet windows are on screen, by hub connection, so a phone can be quiet while you're at the desk. Each
/// window reports itself (visible or not, desktop or phone) every 30 seconds and when its visibility changes; a report
/// counts for <see cref="Lifetime"/>, so a laptop that went to sleep stops counting on its own.
/// </summary>
public sealed class DeskPresenceTracker(TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(90);

    public const string Desktop = "desktop";
    public const string Phone = "phone";

    private readonly ConcurrentDictionary<string, Presence> _connections = new(StringComparer.Ordinal);

    public void Set(string connectionId, bool visible, string formFactor) =>
        _connections[connectionId] = new Presence(visible, formFactor == Phone ? Phone : Desktop, time.GetUtcNow());

    public void Remove(string connectionId) => _connections.TryRemove(connectionId, out _);

    /// <summary>Whether a computer has Fleet on screen right now.</summary>
    public bool AnyDesktopVisible
    {
        get
        {
            var now = time.GetUtcNow();
            foreach (var (id, presence) in _connections)
            {
                if (now - presence.At > Lifetime)
                {
                    _connections.TryRemove(id, out _);
                    continue;
                }

                if (presence.Visible && presence.FormFactor == Desktop)
                    return true;
            }

            return false;
        }
    }

    private sealed record Presence(bool Visible, string FormFactor, DateTimeOffset At);
}

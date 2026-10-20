using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Events;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Usage;

/// <summary>
/// What each signed-in provider says about its usage limits, for the status bar and the limits popover. Fleet asks a
/// provider only when someone could be looking (the popover is open: every 60 seconds) or the work is running (a session
/// on one of its harnesses is busy: every 5 minutes); otherwise the last answer stays, with when it was read. The user
/// allows each provider but Claude, whose sign-in belongs to Claude Code. A 429 or a 401 makes Fleet ask less often for a
/// while. Claude's own live report (<see cref="HarnessUsageLimits"/>) wins over a polled answer when it's newer. Off unless
/// the <c>UsageLimits</c> switch is on: then nothing is read and nothing is sent. Tokens stay in the readers.
/// </summary>
public sealed partial class UsageLimitsService(
    IEnumerable<IUsageProvider> providers,
    HarnessUsageLimits live,
    SessionActivityTracker activity,
    IEventBroadcaster broadcaster,
    IServiceScopeFactory scopeFactory,
    TimeProvider time,
    ILogger<UsageLimitsService> logger)
{
    /// <summary>The preference that turns usage limits on.</summary>
    public const string FlagKey = "UsageLimits";

    /// <summary>Prefix of the preference holding the user's Allow for one provider.</summary>
    public const string AllowPrefix = "UsageAllow.";

    /// <summary>The event carrying <see cref="UsageLimitsSnapshot"/> after something changed.</summary>
    public const string ChangedEvent = "usage.limits";

    /// <summary>An answer this young is reused, whoever asks.</summary>
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);

    /// <summary>How often a busy session's provider is asked.</summary>
    public static readonly TimeSpan BusyEvery = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan FirstBackOff = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LongestBackOff = TimeSpan.FromMinutes(60);

    private sealed class Entry : IDisposable
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public UsageReading? Reading;
        public DateTimeOffset? ReadAt;
        public DateTimeOffset BackOffUntil;
        public int Failures;

        public void Dispose() => Gate.Dispose();
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _lastSent = new(StringComparer.Ordinal);

    /// <summary>The usage as it is. With <paramref name="watch"/> (the popover is open) any answer over 60 seconds old is asked for again.</summary>
    public async Task<UsageLimitsSnapshot> GetAsync(string userId, bool watch, CancellationToken ct)
    {
        if (!await IsOnAsync(ct).ConfigureAwait(false))
            return new UsageLimitsSnapshot(false, []);

        // Popover open: anything over 60 s old is asked again. Otherwise only a provider never asked yet, so the chip
        // has something to show on a fresh page without polling for it.
        var refreshed = false;
        foreach (var provider in providers)
            refreshed |= await RefreshAsync(provider, watch ? CacheFor : TimeSpan.MaxValue, force: false, ct).ConfigureAwait(false);

        var snapshot = await BuildAsync(userId, ct).ConfigureAwait(false);
        if (refreshed)
            await PublishAsync(userId, snapshot, ct).ConfigureAwait(false);
        return snapshot;
    }

    /// <summary>The user pressed Allow (<paramref name="allowed"/>) or revoked it: the provider is asked now, or its answer is hidden.</summary>
    public async Task<bool> SetAllowedAsync(string userId, string provider, bool allowed, CancellationToken ct)
    {
        if (providers.FirstOrDefault(p => p.Provider == provider) is not { } found)
            return false;

        using (var scope = scopeFactory.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IUserPreferenceRepository>()
                .SetAsync(AllowPrefix + provider, allowed ? "true" : "false").ConfigureAwait(false);

        if (!await IsOnAsync(ct).ConfigureAwait(false))
            return true;

        if (allowed)
            await RefreshAsync(found, CacheFor, force: true, ct).ConfigureAwait(false);

        await PublishAsync(userId, await BuildAsync(userId, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>The users with a session working now.</summary>
    public IReadOnlyList<string> BusyUsers()
        => [.. activity.GetAll().Values.Where(s => SessionActivityTracker.IsWorking(s.ActivityStatus) && s.UserId is not null)
            .Select(s => s.UserId!).Distinct(StringComparer.Ordinal)];

    /// <summary>The background tick for one user: asks every provider whose harness has a busy session and whose answer is 5 minutes old.</summary>
    public async Task PollBusyAsync(string userId, CancellationToken ct)
    {
        if (!await IsOnAsync(ct).ConfigureAwait(false))
            return;

        var busy = await BusyHarnessesAsync(userId, ct).ConfigureAwait(false);
        var refreshed = false;
        foreach (var provider in providers.Where(p => p.Harnesses.Any(busy.Contains)))
            refreshed |= await RefreshAsync(provider, BusyEvery, force: false, ct).ConfigureAwait(false);

        if (refreshed)
            await PublishAsync(userId, await BuildAsync(userId, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
    }

    private async Task<HashSet<string>> BusyHarnessesAsync(string userId, CancellationToken ct)
    {
        var harnesses = new HashSet<string>(StringComparer.Ordinal);
        using var scope = scopeFactory.CreateScope();
        var sessions = scope.ServiceProvider.GetRequiredService<ISessionRepository>();
        foreach (var snapshot in activity.GetAll().Values.Where(s => s.UserId == userId && SessionActivityTracker.IsWorking(s.ActivityStatus)))
        {
            ct.ThrowIfCancellationRequested();
            if (await sessions.GetByIdAsync(snapshot.FleetSessionId).ConfigureAwait(false) is { } session)
                harnesses.Add(session.HarnessType);
        }

        return harnesses;
    }

    private async Task<bool> IsOnAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var scope = scopeFactory.CreateScope();
        var value = await scope.ServiceProvider.GetRequiredService<IUserPreferenceRepository>().GetAsync(FlagKey).ConfigureAwait(false);
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> IsAllowedAsync(IUsageProvider provider)
    {
        if (!provider.NeedsConsent)
            return true;
        using var scope = scopeFactory.CreateScope();
        var value = await scope.ServiceProvider.GetRequiredService<IUserPreferenceRepository>().GetAsync(AllowPrefix + provider.Provider).ConfigureAwait(false);
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Asks the provider when it's allowed, signed in, not backing off and its answer is older than <paramref name="maxAge"/>. True when it asked.</summary>
    private async Task<bool> RefreshAsync(IUsageProvider provider, TimeSpan maxAge, bool force, CancellationToken ct)
    {
        if (!await provider.HasSignInAsync(ct).ConfigureAwait(false) || !await IsAllowedAsync(provider).ConfigureAwait(false))
            return false;

        var entry = _entries.GetOrAdd(provider.Provider, _ => new Entry());
        await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = time.GetUtcNow();
            if (!force && (now < entry.BackOffUntil || (entry.ReadAt is { } at && now - at < maxAge)))
                return false;
            if (force)
            {
                entry.BackOffUntil = default;
                entry.Failures = 0;
            }

            UsageReading reading;
            try
            {
                reading = await provider.ReadAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogReadFailed(ex, provider.Provider);
                reading = new UsageReading(UsageStates.Unreadable);
            }

            entry.Reading = reading;
            entry.ReadAt = time.GetUtcNow();
            if (reading.BackOff)
            {
                entry.Failures++;
                var wait = TimeSpan.FromTicks(Math.Min(LongestBackOff.Ticks, FirstBackOff.Ticks << Math.Min(entry.Failures - 1, 6)));
                entry.BackOffUntil = entry.ReadAt.Value + wait;
            }
            else
            {
                entry.Failures = 0;
                entry.BackOffUntil = default;
            }

            return true;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private async Task<UsageLimitsSnapshot> BuildAsync(string userId, CancellationToken ct)
    {
        var reported = live.Get(userId);
        var result = new List<ProviderUsage>();
        foreach (var provider in providers.OrderBy(p => p.Provider, StringComparer.Ordinal))
        {
            var liveWindows = reported.Where(h => provider.Harnesses.Contains(h.HarnessType)).ToList();
            if (!await provider.HasSignInAsync(ct).ConfigureAwait(false) && liveWindows.Count == 0)
                continue;

            _entries.TryGetValue(provider.Provider, out var entry);
            var allowed = await IsAllowedAsync(provider).ConfigureAwait(false);
            var reading = !allowed ? new UsageReading(UsageStates.NotAllowed)
                : entry?.Reading ?? new UsageReading(UsageStates.Unreadable);
            var readAt = allowed ? entry?.ReadAt : null;
            var windows = reading.Windows ?? [];
            var state = reading.State;

            if (allowed && liveWindows.Count > 0 && liveWindows.Max(h => h.UpdatedAt) is var liveAt && (readAt is null || liveAt > readAt))
            {
                var merged = windows.ToDictionary(w => w.Window, StringComparer.Ordinal);
                foreach (var window in liveWindows.SelectMany(h => h.Windows))
                    merged[window.Window] = window;
                windows = [.. merged.Values.OrderBy(w => w.Window, StringComparer.Ordinal)];
                state = UsageStates.Ok;
            }

            result.Add(new ProviderUsage(provider.Provider, provider.Name, state, provider.Harnesses, windows,
                reading.Account, provider.Source, readAt, reading.Credits));
        }

        return new UsageLimitsSnapshot(true, result);
    }

    private async Task PublishAsync(string userId, UsageLimitsSnapshot snapshot, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToElement(snapshot, ApplicationJsonContext.Default.UsageLimitsSnapshot);
        var text = payload.GetRawText();
        if (_lastSent.TryGetValue(userId, out var before) && before == text)
            return;
        _lastSent[userId] = text;
        try
        {
            await broadcaster.BroadcastAsync("sessions", ChangedEvent, payload, userId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogBroadcastFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reading the usage limits of {Provider} failed")]
    private partial void LogReadFailed(Exception ex, string provider);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't tell clients the usage limits changed")]
    private partial void LogBroadcastFailed(Exception ex);
}

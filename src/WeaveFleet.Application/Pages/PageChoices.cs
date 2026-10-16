using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Pages;

/// <summary>Why a page's wait ended without a pick. The agent reads these words, and the page's bar says them.</summary>
public static class PageChoiceReasons
{
    public const string Skipped = "skipped";
    public const string WroteInstead = "wrote instead";
    public const string Stopped = "stopped";
    public const string Restarted = "Fleet restarted";
    public const string TimedOut = "timed out";
}

/// <summary>How a page's wait ended: the pick the user sent, or why there's none.</summary>
public sealed record PageChoiceAnswer(bool Answered, string? Value = null, string? Summary = null, string? Reason = null)
{
    public static PageChoiceAnswer Picked(string value, string summary) => new(true, value, summary);

    public static PageChoiceAnswer NotAnswered(string reason) => new(false, Reason: reason);
}

/// <summary>
/// Choosing on a page: <c>fleet_page_show</c> with <c>waitForChoice</c>, the call waiting for the user's pick. Experimental,
/// off unless the user turns it on in Settings.
/// </summary>
public sealed class PageChoiceFeature(IUserPreferenceRepository preferences)
{
    public const string PreferenceKey = "PageChoice";

    /// <summary>Set to <c>1</c> for an OpenCode process started with it on: its plugin offers <c>waitForChoice</c>.</summary>
    public const string EnvironmentVariable = "FLEET_PAGE_CHOICE";

    public async Task<bool> IsEnabledAsync()
        => string.Equals(await preferences.GetAsync(PreferenceKey).ConfigureAwait(false), "true", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The pages waiting for the user's pick, one per tool call. A wait outlives the requests the plugin makes for it: the
/// plugin asks again every <see cref="Round"/>, since OpenCode cuts a request held for about five minutes. While a page
/// waits, the session waits on the user (<c>waiting_input</c>), as it does on a harness's question. A wait ends when the
/// user sends or skips the pick, writes a message instead or stops the turn, and after <see cref="Cap"/> at most. When
/// Fleet shuts down, every wait ends at once as <see cref="PageChoiceReasons.Restarted"/>, so the pages say so and the
/// held calls don't keep Fleet from stopping.
/// </summary>
public sealed class PageChoices
{
    public static readonly TimeSpan Round = TimeSpan.FromSeconds(50);

    public static readonly TimeSpan Cap = TimeSpan.FromHours(24);

    private sealed record Wait(DateTimeOffset Expires, string Title)
    {
        public TaskCompletionSource<PageChoiceAnswer> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly ConcurrentDictionary<(string Session, string Call), Wait> _waits = new();
    private readonly FleetSessionEvents _events;
    private readonly SessionActivityTracker _activity;
    private readonly TimeProvider _clock;
    private readonly CancellationToken _stopping;

    public PageChoices(FleetSessionEvents events, SessionActivityTracker activity, TimeProvider clock, IHostApplicationLifetime? lifetime = null)
    {
        _events = events;
        _activity = activity;
        _clock = clock;
        _stopping = lifetime?.ApplicationStopping ?? CancellationToken.None;
        _stopping.Register(() => EndAll(PageChoiceAnswer.NotAnswered(PageChoiceReasons.Restarted)));
    }

    /// <summary>Why a wait whose call went away ended: the turn stopped, or Fleet is shutting down.</summary>
    public string CutReason => _stopping.IsCancellationRequested ? PageChoiceReasons.Restarted : PageChoiceReasons.Stopped;

    /// <summary>Starts the call's wait, and the session waits on the user. False when the call waits already.</summary>
    public bool Start(string sessionId, string callId, string title)
    {
        if (!_waits.TryAdd((sessionId, callId), new Wait(_clock.GetUtcNow() + Cap, title)))
            return false;
        _events.RaiseStatus(sessionId, ActivityStatuses.WaitingInput);
        return true;
    }

    /// <summary>The title of a page that waits for the user's pick in the session, or null when none waits.</summary>
    public string? WaitingTitle(string sessionId)
        => _waits.FirstOrDefault(pair => pair.Key.Session == sessionId && !pair.Value.Answer.Task.IsCompleted).Value?.Title;

    /// <summary>Ends the call's wait with <paramref name="answer"/>. False when it isn't waiting, or has its answer already.</summary>
    public bool End(string sessionId, string callId, PageChoiceAnswer answer)
        => _waits.TryGetValue((sessionId, callId), out var wait) && wait.Answer.TrySetResult(answer);

    /// <summary>Ends every wait in the session, as when the user writes instead; how many it ended.</summary>
    public int EndAll(string sessionId, PageChoiceAnswer answer)
        => _waits.Count(pair => pair.Key.Session == sessionId && pair.Value.Answer.TrySetResult(answer));

    private void EndAll(PageChoiceAnswer answer)
    {
        foreach (var wait in _waits.Values)
            wait.Answer.TrySetResult(answer);
    }

    /// <summary>
    /// The call's answer when it comes within <paramref name="round"/>, or null when the call still waits. A call Fleet
    /// doesn't know waited from before Fleet restarted: <see cref="PageChoiceReasons.Restarted"/>.
    /// </summary>
    public async Task<PageChoiceAnswer?> NextAsync(string sessionId, string callId, TimeSpan round, CancellationToken ct)
    {
        if (!_waits.TryGetValue((sessionId, callId), out var wait))
            return PageChoiceAnswer.NotAnswered(PageChoiceReasons.Restarted);

        var left = wait.Expires - _clock.GetUtcNow();
        if (left > TimeSpan.Zero)
            await Task.WhenAny(wait.Answer.Task, Task.Delay(left < round ? left : round, _clock, ct)).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (_clock.GetUtcNow() >= wait.Expires)
            wait.Answer.TrySetResult(PageChoiceAnswer.NotAnswered(PageChoiceReasons.TimedOut));
        return wait.Answer.Task.IsCompleted ? await wait.Answer.Task.ConfigureAwait(false) : null;
    }

    /// <summary>
    /// Forgets the call once its page says how the wait ended. The turn works again unless another page still waits; not
    /// after a stop, when the harness says itself that the turn is over.
    /// </summary>
    public void Finish(string sessionId, string callId, PageChoiceAnswer answer)
    {
        if (!_waits.TryRemove((sessionId, callId), out _)
            || answer.Reason is PageChoiceReasons.Stopped or PageChoiceReasons.Restarted
            || _waits.Keys.Any(key => key.Session == sessionId))
            return;

        // Only over Fleet's own status: whatever the harness said since is newer.
        if (_activity.Get(sessionId)?.ActivityStatus == ActivityStatuses.WaitingInput)
            _events.RaiseStatus(sessionId, ActivityStatuses.Busy);
    }

    /// <summary>Forgets the session's waits: its harness process went away, and the calls with it.</summary>
    public void Forget(string sessionId)
    {
        foreach (var key in _waits.Keys.Where(key => key.Session == sessionId))
        {
            if (_waits.TryRemove(key, out var wait))
                wait.Answer.TrySetResult(PageChoiceAnswer.NotAnswered(CutReason));
        }
    }
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
[System.Text.Json.Serialization.JsonSerializable(typeof(PageChoiceAnswer))]
internal sealed partial class PagesJsonContext : System.Text.Json.Serialization.JsonSerializerContext;

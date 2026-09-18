using System.Net;
using System.Text.Json;
using WeaveFleet.Application.Canvases;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Sessions;

/// <summary>
/// Messages between sessions: one session's agent tells another session something with the <c>fleet_message</c>
/// tool. The message says which session sent it, as Fleet resolved it from the calling process, so the receiver
/// can treat it as a teammate's request rather than the user's. Experimental, and off unless turned on.
/// </summary>
public static class SessionMessages
{
    /// <summary>The user preference that turns it on or off; when unset, <see cref="HarnessOptions.SessionMessages"/> decides.</summary>
    public const string PreferenceKey = "SessionMessages";

    /// <summary>Set to <c>1</c> in a harness process's environment when it was started with messages on.</summary>
    public const string EnvironmentVariable = "FLEET_SESSION_MESSAGES";

    /// <summary>
    /// A harness process started with messages on reaches Fleet at <c>{FLEET_URL}</c> = <c>{fleet}/agent/{bridge token}</c>,
    /// so Fleet can tell its API calls from the user's.
    /// </summary>
    public const string AgentPathPrefix = "/agent";

    /// <summary>What the API answers an agent that tries to prompt a session while messages are on.</summary>
    public const string UseTheToolMessage = "Use the fleet_message tool to message a session. Fleet doesn't take prompts from agents through its API.";

    private const string Tag = "fleet-session-message";

    /// <summary>
    /// The prompt text a message arrives as: the sender's id and title in the opening tag, then the text. The tag
    /// travels in the text itself, so it survives a reload of the conversation from the harness's store.
    /// </summary>
    public static string Wrap(string fromSessionId, string fromTitle, string text)
        => $"<{Tag} from=\"{WebUtility.HtmlEncode(fromSessionId)}\" title=\"{WebUtility.HtmlEncode(fromTitle)}\">\n{text}\n</{Tag}>";
}

/// <summary>Whether messages between sessions are on for the current user.</summary>
public sealed class SessionMessagesFeature(FleetOptions options, IUserPreferenceRepository preferences)
{
    public async Task<bool> IsEnabledAsync()
    {
        var value = await preferences.GetAsync(SessionMessages.PreferenceKey).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(value)
            ? options.Harness.SessionMessages
            : string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>The bridge tokens of the harness processes Fleet is running, for telling their API calls apart.</summary>
public interface IHarnessBridgeTokens
{
    bool IsKnown(string bridgeToken);
}

/// <summary>
/// The <c>fleet_message</c> tool, for calls from a harness process. The sender is the session the call resolves to
/// through <see cref="IHarnessCanvasCallerResolver"/>, the same way the canvas tools find theirs.
/// </summary>
public sealed class SessionMessageBridge(
    IHarnessCanvasCallerResolver callers,
    IBackgroundUserScope userScope,
    SessionMessagesFeature feature,
    SessionService sessions,
    SessionOrchestrator orchestrator,
    IEventBroadcaster broadcaster)
{
    public const string TurnedOffMessage = "Messages between sessions are turned off in Fleet's Settings.";

    public async Task<CanvasResult<CanvasToolOutput>> SendAsync(
        string? bridgeToken,
        string? harnessSessionId,
        string? toSessionId,
        string? text,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(bridgeToken) || string.IsNullOrWhiteSpace(harnessSessionId))
            return UnknownCaller();

        var caller = await callers.ResolveAsync(bridgeToken, harnessSessionId, ct).ConfigureAwait(false);
        if (caller is null)
            return UnknownCaller();

        using (userScope.Begin(caller.UserId))
        {
            // Checked on every call: a process started while it was on keeps the tool until it's recycled.
            if (!await feature.IsEnabledAsync().ConfigureAwait(false))
                return CanvasResult.Fail<CanvasToolOutput>(CanvasErrorKind.Refused, TurnedOffMessage);

            if (string.IsNullOrWhiteSpace(toSessionId))
                return Invalid("\"sessionId\" is required: the Fleet id of the session to message, from GET $FLEET_URL/api/sessions.");
            if (string.IsNullOrWhiteSpace(text))
                return Invalid("\"text\" is required.");
            if (string.Equals(toSessionId, caller.FleetSessionId, StringComparison.Ordinal))
                return Invalid("That's this session. Give the id of another one.");

            var from = await sessions.GetSessionAsync(caller.FleetSessionId).ConfigureAwait(false);
            var to = await sessions.GetSessionAsync(toSessionId).ConfigureAwait(false);
            if (from.IsFailure)
                return UnknownCaller();
            if (to.IsFailure)
                return CanvasResult.Fail<CanvasToolOutput>(
                    CanvasErrorKind.NotFound,
                    $"No session {toSessionId}. Find session ids with GET $FLEET_URL/api/sessions.");

            var sent = await orchestrator.PromptSessionWithReceiptAsync(
                    toSessionId,
                    SessionMessages.Wrap(caller.FleetSessionId, from.Value.Title, text.Trim()),
                    options: null,
                    userMessageId: null,
                    correlationId: null,
                    ct)
                .ConfigureAwait(false);
            if (sent.IsFailure)
                return CanvasResult.Fail<CanvasToolOutput>(CanvasErrorKind.Refused, $"Fleet couldn't deliver it: {sent.Error.Description}");

            var payload = new SessionMessagedPayload
            {
                FromSessionId = caller.FleetSessionId,
                ToSessionId = toSessionId,
                EventId = sent.Value.EventId,
                CorrelationId = sent.Value.CorrelationId,
            };
            await broadcaster.BroadcastAsync(
                    $"session:{toSessionId}",
                    "session.messaged",
                    JsonSerializer.SerializeToElement(payload, ApplicationJsonContext.Default.SessionMessagedPayload),
                    new SessionMessaged { Payload = payload },
                    caller.UserId,
                    ct)
                .ConfigureAwait(false);

            var title = to.Value.Title;
            return CanvasResult.Ok(new CanvasToolOutput(
                $"Messaged {title}",
                $"Delivered to {title} ({toSessionId}). It starts on it now, or when the turn it's on ends. Its reply stays in that session."));
        }
    }

    private static CanvasResult<CanvasToolOutput> UnknownCaller()
        => CanvasResult.Fail<CanvasToolOutput>(CanvasErrorKind.NotFound, CanvasBridge.UnknownCallerMessage);

    private static CanvasResult<CanvasToolOutput> Invalid(string message)
        => CanvasResult.Fail<CanvasToolOutput>(CanvasErrorKind.Invalid, message);
}

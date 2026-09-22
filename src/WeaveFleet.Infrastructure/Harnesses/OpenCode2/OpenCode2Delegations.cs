using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Services;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode2;

/// <summary>
/// Records one Fleet session's subagent calls as delegations, and makes each call's child session a Fleet session of
/// its own (hidden, under the parent) that attaches to the same server and shows the child's activity and questions.
/// </summary>
/// <remarks>
/// Updates are handled one at a time in the order V2 sent them, off the event stream: creating the child session takes
/// a moment, and a "running" update handled after "completed" would leave the subagent running for good.
/// </remarks>
internal sealed partial class OpenCode2Delegations(
    IServiceScopeFactory scopeFactory,
    string ownerUserId,
    string fleetSessionId,
    ILogger logger)
{
    private readonly object _sync = new();
    private Task _tail = Task.CompletedTask;

    /// <summary>Completes when every update queued so far is handled.</summary>
    internal Task Idle
    {
        get
        {
            lock (_sync)
                return _tail;
        }
    }

    public void Queue(OpenCode2Delegation delegation)
    {
        lock (_sync)
        {
            _tail = _tail
                .ContinueWith(_ => HandleAsync(delegation), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default)
                .Unwrap();
        }
    }

    private async Task HandleAsync(OpenCode2Delegation update)
    {
        try
        {
            using var user = BackgroundUserContext.BeginScope(ownerUserId);
            using var scope = scopeFactory.CreateScope();
            var delegations = scope.ServiceProvider.GetRequiredService<DelegationService>();

            var delegation = await delegations.HandleDelegationDetectedAsync(
                fleetSessionId, update.ToolCallId, update.Agent, update.Description).ConfigureAwait(false);

            if (update.ChildSessionId is { } childSessionId)
            {
                var orchestrator = scope.ServiceProvider.GetRequiredService<SessionOrchestrator>();
                var child = await orchestrator.EnsureDelegatedChildSessionAsync(
                    fleetSessionId, childSessionId, update.Description ?? update.Agent).ConfigureAwait(false);
                if (child.IsFailure)
                {
                    // The call still ends like any other; only the link to its activity is missing.
                    LogChildFailed(logger, fleetSessionId, childSessionId, child.Error.Description);
                }
                else if (delegation.Status is "pending" or "running")
                {
                    // Only a delegation still under way takes its child; a finished one already has it.
                    delegation = await delegations.HandleChildLinkedAsync(
                        fleetSessionId, update.ToolCallId, child.Value.Id).ConfigureAwait(false) ?? delegation;
                }
            }

            if (update.Status is "completed" or "error" or "cancelled" && delegation.Status is "pending" or "running")
                await delegations.HandleDelegationFinishedAsync(delegation.DelegationId, update.Status).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogFailed(logger, fleetSessionId, update.ToolCallId, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't record subagent call {ToolCallId} of session {SessionId} as a delegation")]
    private static partial void LogFailed(ILogger logger, string sessionId, string toolCallId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't create a Fleet session for OpenCode 2 child session {ChildSessionId} of session {SessionId}: {Reason}")]
    private static partial void LogChildFailed(ILogger logger, string sessionId, string childSessionId, string reason);
}

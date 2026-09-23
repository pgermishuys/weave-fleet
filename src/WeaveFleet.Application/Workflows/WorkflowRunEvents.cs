using System.Text.Json;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.Sessions;

namespace WeaveFleet.Application.Workflows;

/// <summary>
/// Pushes each change to a run on the global sessions topic as <c>workflow_run</c>, and uses the desktop notification
/// the user already has when a run stops on them.
/// </summary>
public sealed class WorkflowRunEvents(IEventBroadcaster broadcaster, SessionNotifier notifier) : IWorkflowRunEvents
{
    /// <summary>The event type on the global sessions topic.</summary>
    public const string EventType = "workflow_run";

    public Task ChangedAsync(WorkflowRunDto run, string userId)
        => broadcaster.BroadcastAsync(
            "sessions",
            EventType,
            JsonSerializer.SerializeToElement(run, WorkflowJsonContext.Default.WorkflowRunDto),
            userId,
            CancellationToken.None);

    public Task NeedsYouAsync(WorkflowRunDto run, string userId)
    {
        if (run.Waiting is { SessionId: { } sessionId } waiting)
            notifier.OnWorkflowNeedsYou(sessionId, waiting.Kind == WorkflowWaitingKinds.You ? $"{waiting.StepTitle}: {waiting.Question}" : waiting.Message);
        return Task.CompletedTask;
    }
}

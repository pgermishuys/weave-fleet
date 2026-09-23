using WeaveFleet.Application.Canvases;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Application.Workflows;

/// <summary>
/// The <see cref="Workflows.StepTool"/> tool, for calls from a harness process. Only the session Fleet started for a
/// step can finish it: not a subagent it delegated to (whose calls the resolver finds through its parent), and not any
/// other session that happens to see the tool.
/// </summary>
public sealed class WorkflowStepBridge(
    IEnumerable<IHarnessCanvasCallerResolver> callers,
    IBackgroundUserScope userScope,
    WorkflowsFeature feature,
    WorkflowRunner runner)
{
    public const string NotAStepMessage =
        "Only the session Fleet started for a workflow step can finish it. If you're working on a task another agent gave you, report back to that agent instead.";

    public async Task<CanvasResult<CanvasToolOutput>> DoneAsync(
        string? bridgeToken,
        string? harnessSessionId,
        string? outcome,
        string? summary,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(bridgeToken) || string.IsNullOrWhiteSpace(harnessSessionId))
            return UnknownCaller();

        var caller = await callers.ResolveAsync(bridgeToken, harnessSessionId, ct).ConfigureAwait(false);
        if (caller is null)
            return UnknownCaller();

        if (caller.ViaParent)
            return CanvasResult.Fail<CanvasToolOutput>(CanvasErrorKind.Refused, NotAStepMessage);

        using (userScope.Begin(caller.UserId))
        {
            // Checked on every call: a process started while it was on keeps the tool until it's recycled.
            if (!await feature.IsEnabledAsync().ConfigureAwait(false))
                return CanvasResult.Fail<CanvasToolOutput>(CanvasErrorKind.Refused, Workflows.TurnedOffMessage);

            var done = await runner.StepDoneAsync(caller.UserId, caller.FleetSessionId, outcome, summary, ct).ConfigureAwait(false);
            return done.Accepted
                ? CanvasResult.Ok(new CanvasToolOutput("Step done", done.Message))
                : CanvasResult.Fail<CanvasToolOutput>(CanvasErrorKind.Refused, done.Message);
        }
    }

    private static CanvasResult<CanvasToolOutput> UnknownCaller()
        => CanvasResult.Fail<CanvasToolOutput>(CanvasErrorKind.NotFound, CanvasBridge.UnknownCallerMessage);
}

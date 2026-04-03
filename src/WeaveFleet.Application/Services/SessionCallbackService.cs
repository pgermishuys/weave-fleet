using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Services;

/// <summary>
/// Manages session completion callbacks — notifying a parent/conductor session when a child session finishes.
/// Mirrors the TypeScript callback-service.ts + callback-monitor.ts logic.
/// </summary>
public sealed partial class SessionCallbackService(
    ISessionCallbackRepository callbackRepository,
    ISessionRepository sessionRepository,
    InstanceTracker instanceTracker,
    ILogger<SessionCallbackService> logger)
{
    /// <summary>
    /// Registers a callback: when <paramref name="sourceSessionId"/> completes,
    /// prompt the target instance.
    /// </summary>
    public async Task<SessionCallback> RegisterCallbackAsync(
        string sourceSessionId,
        string targetSessionId,
        string targetInstanceId)
    {
        var callback = new SessionCallback
        {
            Id = Guid.NewGuid().ToString(),
            SourceSessionId = sourceSessionId,
            TargetSessionId = targetSessionId,
            TargetInstanceId = targetInstanceId,
            Status = "pending",
            CreatedAt = DateTime.UtcNow.ToString("O")
        };

        await callbackRepository.InsertAsync(callback);
        return callback;
    }

    /// <summary>
    /// Called when <paramref name="sourceSessionId"/> completes.
    /// Fires all pending callbacks for that source session.
    /// Returns the number of callbacks fired.
    /// </summary>
    public async Task<int> TryFireCallbacksAsync(string sourceSessionId, CancellationToken ct = default)
    {
        var pending = await callbackRepository.GetPendingForSessionAsync(sourceSessionId);
        if (pending.Count == 0)
            return 0;

        var source = await sessionRepository.GetByIdAsync(sourceSessionId);
        var completionSummary = source is not null
            ? $"Session '{source.Title}' completed."
            : "A background session completed.";

        var fired = 0;
        foreach (var cb in pending)
        {
            // Try to claim the callback atomically to avoid duplicate firing
            var claimed = await callbackRepository.ClaimPendingAsync(cb.Id);
            if (!claimed)
                continue;

            var targetInstance = instanceTracker.Get(cb.TargetInstanceId);
            if (targetInstance is null)
            {
                LogTargetNotFound(cb.Id, cb.TargetInstanceId);
                continue;
            }

            try
            {
                await targetInstance.SendPromptAsync(completionSummary, null, ct);
                await callbackRepository.MarkFiredAsync(cb.Id);
                fired++;
                LogCallbackFired(cb.Id, cb.TargetSessionId);
            }
            catch (Exception ex)
            {
                LogCallbackFailed(ex, cb.Id);
            }
        }

        return fired;
    }

    /// <summary>
    /// Poll-based safety net: fires any pending callbacks where the target instance is live.
    /// Useful to recover callbacks that were registered but never triggered.
    /// </summary>
    public async Task<int> ProcessPendingCallbacksAsync(CancellationToken ct = default)
    {
        var allPending = await callbackRepository.GetAllPendingAsync();
        if (allPending.Count == 0)
            return 0;

        var fired = 0;
        foreach (var cb in allPending)
        {
            // Check whether source session is in a terminal state
            var source = await sessionRepository.GetByIdAsync(cb.SourceSessionId);
            if (source is null || source.Status is not ("stopped" or "completed"))
                continue;

            fired += await TryFireCallbacksAsync(cb.SourceSessionId, ct);
        }

        return fired;
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Callback {CallbackId}: target instance {InstanceId} is not live; skipping.")]
    private partial void LogTargetNotFound(string callbackId, string instanceId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Callback {CallbackId} fired → target session {TargetSessionId}.")]
    private partial void LogCallbackFired(string callbackId, string targetSessionId);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Callback {CallbackId} failed to send prompt.")]
    private partial void LogCallbackFailed(Exception ex, string callbackId);
}

using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Services;

/// <summary>
/// Background service that subscribes to domain events and triggers matching automations.
/// <para>
/// Receives domain events via a <see cref="Channel{T}"/>, matches them against enabled
/// automations, deduplicates via <see cref="IAutomationEventLedgerRepository"/>, and
/// executes matched automations via <see cref="AutomationExecutionService"/>.
/// </para>
/// <para>
/// Feedback-loop guard: events originating from sessions with <c>source_reference</c>
/// starting with "automation:" are skipped to prevent infinite recursion.
/// </para>
/// <para>
/// Errors processing individual events are logged but do not crash the service.
/// </para>
/// </summary>
/// <remarks>
/// The channel wiring is handled by the event bus infrastructure (task 19).
/// </remarks>
public sealed partial class AutomationEventDispatcherService : BackgroundService
{
    private readonly Channel<AutomationEventNotification> _eventChannel;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AutomationEventDispatcherService> _logger;

    public AutomationEventDispatcherService(
        Channel<AutomationEventNotification> eventChannel,
        IServiceScopeFactory scopeFactory,
        ILogger<AutomationEventDispatcherService> logger)
    {
        _eventChannel = eventChannel;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogServiceStarted();

        await foreach (var notification in _eventChannel.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await ProcessEventAsync(notification, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogEventProcessingFailed(ex, notification.EventType, notification.EventId);
            }
        }

        LogServiceStopped();
    }

    private async Task ProcessEventAsync(AutomationEventNotification notification, CancellationToken ct)
    {
        // Feedback-loop guard: skip events from automation-created sessions
        if (notification.SessionSourceReference?.StartsWith("automation:", StringComparison.OrdinalIgnoreCase) == true)
        {
            LogSkippedAutomationLoop(notification.EventType, notification.EventId, notification.SessionSourceReference);
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var matcher = scope.ServiceProvider.GetRequiredService<EventTriggerMatcher>();
        var ledger = scope.ServiceProvider.GetRequiredService<IAutomationEventLedgerRepository>();
        var executor = scope.ServiceProvider.GetRequiredService<AutomationExecutionService>();
        var sessionRepo = scope.ServiceProvider.GetRequiredService<ISessionRepository>();

        // Find matching automations
        var matchingAutomations = await matcher.FindMatchingAutomationsAsync(notification.EventType, ct).ConfigureAwait(false);

        if (matchingAutomations.Count == 0)
        {
            LogNoMatchingAutomations(notification.EventType, notification.EventId);
            return;
        }

        // Load session tags if SessionId is present
        List<string>? sessionTags = null;
        if (!string.IsNullOrWhiteSpace(notification.SessionId))
        {
            var session = await sessionRepo.GetByIdAsync(notification.SessionId).ConfigureAwait(false);
            sessionTags = session?.Tags;
        }

        LogFoundMatchingAutomations(notification.EventType, notification.EventId, matchingAutomations.Count);

        // Process each matching automation
        foreach (var automation in matchingAutomations)
        {
            try
            {
                // Filter by target tags: if automation has TargetTags, session must have at least one matching tag
                if (automation.TargetTags.Count > 0)
                {
                    if (sessionTags == null || sessionTags.Count == 0)
                    {
                        LogSkippedNoSessionTags(automation.Id, automation.Name, notification.EventId);
                        continue;
                    }

                    var hasMatchingTag = automation.TargetTags.Any(targetTag =>
                        sessionTags.Contains(targetTag, StringComparer.OrdinalIgnoreCase));

                    if (!hasMatchingTag)
                    {
                        LogSkippedTagMismatch(automation.Id, automation.Name, notification.EventId);
                        continue;
                    }
                }

                // Check if already processed (deduplication)
                var isProcessed = await ledger.IsProcessedAsync(automation.Id, notification.EventId).ConfigureAwait(false);
                if (isProcessed)
                {
                    LogSkippedDuplicate(automation.Id, automation.Name, notification.EventId);
                    continue;
                }

                // Record ledger entry before execution to prevent duplicate processing on retry
                await ledger.RecordAsync(automation.Id, notification.EventId).ConfigureAwait(false);

                // Execute automation
                LogExecutingAutomation(automation.Id, automation.Name, notification.EventType, notification.EventId);
                await executor.ExecuteAsync(
                    automation,
                    eventType: notification.EventType,
                    eventSummary: notification.EventSummary,
                    ct: ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Log error but continue processing other automations
                LogAutomationExecutionFailed(ex, automation.Id, automation.Name, notification.EventId);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Automation event dispatcher service started.")]
    private partial void LogServiceStarted();

    [LoggerMessage(Level = LogLevel.Information, Message = "Automation event dispatcher service stopped.")]
    private partial void LogServiceStopped();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to process event type={EventType} id={EventId}.")]
    private partial void LogEventProcessingFailed(Exception ex, string eventType, string eventId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipped automation-loop event type={EventType} id={EventId} sourceRef={SourceReference}.")]
    private partial void LogSkippedAutomationLoop(string eventType, string eventId, string sourceReference);

    [LoggerMessage(Level = LogLevel.Debug, Message = "No matching automations for event type={EventType} id={EventId}.")]
    private partial void LogNoMatchingAutomations(string eventType, string eventId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Found {Count} matching automation(s) for event type={EventType} id={EventId}.")]
    private partial void LogFoundMatchingAutomations(string eventType, string eventId, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipped duplicate: automation {AutomationId} ({AutomationName}) already processed event {EventId}.")]
    private partial void LogSkippedDuplicate(string automationId, string automationName, string eventId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipped automation {AutomationId} ({AutomationName}) for event {EventId}: session has no tags but automation requires tags.")]
    private partial void LogSkippedNoSessionTags(string automationId, string automationName, string eventId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipped automation {AutomationId} ({AutomationName}) for event {EventId}: session tags do not match automation target tags.")]
    private partial void LogSkippedTagMismatch(string automationId, string automationName, string eventId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Executing automation {AutomationId} ({AutomationName}) for event type={EventType} id={EventId}.")]
    private partial void LogExecutingAutomation(string automationId, string automationName, string eventType, string eventId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to execute automation {AutomationId} ({AutomationName}) for event {EventId}.")]
    private partial void LogAutomationExecutionFailed(Exception ex, string automationId, string automationName, string eventId);
}

/// <summary>
/// Notification of a domain event that may trigger automations.
/// </summary>
/// <param name="EventType">The domain event type (e.g., "session.started", "message.created").</param>
/// <param name="EventId">The unique event identifier for deduplication.</param>
/// <param name="SessionId">The session ID that emitted the event, if any.</param>
/// <param name="SessionSourceReference">The source_reference of the session that emitted the event, if any.</param>
/// <param name="EventSummary">Optional human-readable summary of the event for context.</param>
public sealed record AutomationEventNotification(
    string EventType,
    string EventId,
    string? SessionId,
    string? SessionSourceReference,
    string? EventSummary = null);

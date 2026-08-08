using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.SessionSources;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Services;

/// <summary>
/// Service that executes an automation by creating a session via SessionOrchestrator.
/// </summary>
public sealed partial class AutomationExecutionService(
    SessionOrchestrator sessionOrchestrator,
    ISessionRepository sessionRepository,
    ILogger<AutomationExecutionService> logger)
{
    /// <summary>
    /// Executes an automation by creating a session with the automation's prompt and configuration.
    /// </summary>
    /// <param name="automation">The automation to execute.</param>
    /// <param name="eventType">Optional event type that triggered this execution.</param>
    /// <param name="eventSummary">Optional event summary providing context.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task ExecuteAsync(
        Automation automation,
        string? eventType = null,
        string? eventSummary = null,
        CancellationToken ct = default)
    {
        try
        {
            LogExecutionStarting(automation.Id, automation.Name, eventType);

            // 1. Expand template variables in the prompt
            var expandedPrompt = ExpandTemplateVariables(automation.Prompt, automation.Name);

            // 2. If event-triggered, prepend event context
            var finalPrompt = BuildFinalPrompt(expandedPrompt, eventType, eventSummary);

            // 3. Route based on target type
            var targetType = automation.TargetType ?? "new_session";
            
            switch (targetType)
            {
                case "most_recent_session":
                    await ExecuteOnMostRecentSessionAsync(automation, finalPrompt, ct);
                    break;
                
                case "tagged_session":
                    await ExecuteOnTaggedSessionAsync(automation, finalPrompt, ct);
                    break;
                
                case "new_session":
                default:
                    await ExecuteOnNewSessionAsync(automation, finalPrompt, eventType, ct);
                    break;
            }
        }
        catch (Exception ex)
        {
            // Errors are logged, not thrown — automation execution is fire-and-forget
            LogExecutionException(ex, automation.Id, automation.Name);
        }
    }

    /// <summary>
    /// Executes automation on a new session.
    /// </summary>
    private async Task ExecuteOnNewSessionAsync(
        Automation automation,
        string finalPrompt,
        string? eventType,
        CancellationToken ct)
    {
        var request = new CreateSessionRequest
        {
            Title = $"Automation: {automation.Name}",
            InitialPrompt = finalPrompt,
            ProjectId = null, // Automations use default/scratch project
            HarnessType = null, // Use default harness
            Directory = automation.WorkspaceId, // Use automation's workspace if specified
            IsolationStrategy = string.IsNullOrWhiteSpace(automation.WorkspaceId) ? "clone" : "existing",
            Source = BuildSessionSource(automation, eventType),
            SourceReference = $"automation:{automation.Id}"
        };

        var result = await sessionOrchestrator.CreateSessionAsync(request, ct);

        if (result.IsSuccess)
        {
            LogExecutionSucceeded(automation.Id, automation.Name, result.Value.Session.Id);
        }
        else
        {
            LogExecutionFailed(automation.Id, automation.Name, result.Error.Code, result.Error.Description);
        }
    }

    /// <summary>
    /// Executes automation on the most recent session, falling back to new session if none found.
    /// </summary>
    private async Task ExecuteOnMostRecentSessionAsync(
        Automation automation,
        string finalPrompt,
        CancellationToken ct)
    {
        // Query for the most recent session (limit=1, ordered by created_at DESC)
        var sessions = await sessionRepository.ListAsync(
            limit: 1,
            offset: 0,
            statuses: ["active", "idle"],
            projectId: null,
            retentionStatuses: null,
            tags: null);

        if (sessions.Count > 0)
        {
            var targetSession = sessions[0];
            var result = await sessionOrchestrator.PromptSessionAsync(
                targetSession.Id,
                finalPrompt,
                options: null,
                ct);

            if (result.IsSuccess)
            {
                LogExecutionSucceeded(automation.Id, automation.Name, targetSession.Id);
            }
            else
            {
                LogExecutionFailed(automation.Id, automation.Name, result.Error.Code, result.Error.Description);
            }
        }
        else
        {
            LogNoSessionFoundFallingBack(automation.Id, automation.Name, "most_recent_session");
            await ExecuteOnNewSessionAsync(automation, finalPrompt, eventType: null, ct);
        }
    }

    /// <summary>
    /// Executes automation on the most recent session matching target tags, falling back to new session if none found.
    /// </summary>
    private async Task ExecuteOnTaggedSessionAsync(
        Automation automation,
        string finalPrompt,
        CancellationToken ct)
    {
        if (automation.TargetTags.Count == 0)
        {
            LogNoTagsSpecifiedFallingBack(automation.Id, automation.Name);
            await ExecuteOnNewSessionAsync(automation, finalPrompt, eventType: null, ct);
            return;
        }

        // Query for the most recent session with matching tags
        var sessions = await sessionRepository.ListAsync(
            limit: 1,
            offset: 0,
            statuses: ["active", "idle"],
            projectId: null,
            retentionStatuses: null,
            tags: automation.TargetTags);

        if (sessions.Count > 0)
        {
            var targetSession = sessions[0];
            var result = await sessionOrchestrator.PromptSessionAsync(
                targetSession.Id,
                finalPrompt,
                options: null,
                ct);

            if (result.IsSuccess)
            {
                LogExecutionSucceeded(automation.Id, automation.Name, targetSession.Id);
            }
            else
            {
                LogExecutionFailed(automation.Id, automation.Name, result.Error.Code, result.Error.Description);
            }
        }
        else
        {
            LogNoSessionFoundFallingBack(automation.Id, automation.Name, $"tagged_session (tags: {string.Join(", ", automation.TargetTags)})");
            await ExecuteOnNewSessionAsync(automation, finalPrompt, eventType: null, ct);
        }
    }

    /// <summary>
    /// Builds a SessionSourceSelection for tracking automation provenance.
    /// </summary>
    private static SessionSourceSelection BuildSessionSource(Automation automation, string? eventType)
    {
        // Build input JSON manually to avoid trimming issues
        var inputJson = $$"""
            {
                "automationId": "{{automation.Id}}",
                "automationName": "{{automation.Name}}",
                "trigger": "{{eventType ?? "schedule"}}"
            }
            """;

        return new SessionSourceSelection
        {
            Key = new SessionSourceKey
            {
                ProviderId = "builtin.automation",
                SourceType = "automation",
                ActionId = SessionSourceActions.StartSession
            },
            Input = JsonDocument.Parse(inputJson).RootElement.Clone()
        };
    }

    /// <summary>
    /// Expands template variables in the prompt.
    /// Supported variables: {{name}}, {{timestamp}}
    /// </summary>
    private static string ExpandTemplateVariables(string prompt, string automationName)
    {
        var expanded = prompt;

        // Replace {{name}} with automation name
        expanded = NameTemplateRegex().Replace(expanded, automationName);

        // Replace {{timestamp}} with ISO 8601 UTC timestamp
        expanded = TimestampTemplateRegex().Replace(expanded, DateTime.UtcNow.ToString("O"));

        return expanded;
    }

    /// <summary>
    /// Builds the final prompt by prepending event context if present.
    /// </summary>
    private static string BuildFinalPrompt(string expandedPrompt, string? eventType, string? eventSummary)
    {
        if (string.IsNullOrWhiteSpace(eventType) || string.IsNullOrWhiteSpace(eventSummary))
        {
            return expandedPrompt;
        }

        return $"[Context]\n{eventType}: {eventSummary}\n\n[Instruction]\n{expandedPrompt}";
    }

    [GeneratedRegex(@"\{\{name\}\}", RegexOptions.IgnoreCase)]
    private static partial Regex NameTemplateRegex();

    [GeneratedRegex(@"\{\{timestamp\}\}", RegexOptions.IgnoreCase)]
    private static partial Regex TimestampTemplateRegex();

    [LoggerMessage(Level = LogLevel.Information, Message = "Starting automation execution: {AutomationId} ({AutomationName}), trigger: {EventType}")]
    private partial void LogExecutionStarting(string automationId, string automationName, string? eventType);

    [LoggerMessage(Level = LogLevel.Information, Message = "Automation execution succeeded: {AutomationId} ({AutomationName}), session: {SessionId}")]
    private partial void LogExecutionSucceeded(string automationId, string automationName, string sessionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Automation execution failed: {AutomationId} ({AutomationName}), error: {ErrorCode} - {ErrorMessage}")]
    private partial void LogExecutionFailed(string automationId, string automationName, string errorCode, string errorMessage);

    [LoggerMessage(Level = LogLevel.Error, Message = "Automation execution threw exception: {AutomationId} ({AutomationName})")]
    private partial void LogExecutionException(Exception ex, string automationId, string automationName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No session found for automation {AutomationId} ({AutomationName}) with target type '{TargetType}', falling back to new session")]
    private partial void LogNoSessionFoundFallingBack(string automationId, string automationName, string targetType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No tags specified for automation {AutomationId} ({AutomationName}) with target type 'tagged_session', falling back to new session")]
    private partial void LogNoTagsSpecifiedFallingBack(string automationId, string automationName);
}

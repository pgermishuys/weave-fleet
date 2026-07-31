using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.SessionSources;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Application.Services;

/// <summary>
/// Service that executes an automation by creating a session via SessionOrchestrator.
/// </summary>
public sealed partial class AutomationExecutionService(
    SessionOrchestrator sessionOrchestrator,
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

            // 3. Create session via SessionOrchestrator
            // Use the automation's workspace if specified, otherwise let orchestrator manage it
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
        catch (Exception ex)
        {
            // Errors are logged, not thrown — automation execution is fire-and-forget
            LogExecutionException(ex, automation.Id, automation.Name);
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
}

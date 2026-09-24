using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.SessionSources;
using WeaveFleet.Application.Workflows;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Services;

/// <summary>Which session a run used, or why it couldn't start one.</summary>
/// <param name="WorkflowRunId">The workflow run it started, for a <c>workflow</c> target.</param>
/// <param name="Skipped">Nothing started, on purpose or because the start was refused; <paramref name="Error"/> says why.</param>
public sealed record AutomationExecutionOutcome(string? SessionId, string? InstanceId, string? Error, string? WorkflowRunId = null, bool Skipped = false)
{
    public static AutomationExecutionOutcome Failed(string error) => new(null, null, error);
    public static AutomationExecutionOutcome SkippedBecause(string reason) => new(null, null, reason, Skipped: true);
}

/// <summary>Starts an automation's session; <see cref="AutomationRunService"/> records the run around it.</summary>
public interface IAutomationExecutor
{
    Task<AutomationExecutionOutcome> ExecuteAsync(
        Automation automation,
        string? eventType = null,
        string? eventSummary = null,
        string? previousSessionId = null,
        CancellationToken ct = default);
}

/// <summary>
/// Service that executes an automation by creating a session via SessionOrchestrator.
/// </summary>
public sealed partial class AutomationExecutionService(
    SessionOrchestrator sessionOrchestrator,
    ISessionRepository sessionRepository,
    ILogger<AutomationExecutionService> logger,
    IAutomationWorkflows? workflows = null) : IAutomationExecutor
{
    /// <summary>
    /// Runs an automation: starts a session with its prompt, or prompts the session a target type picks. Never throws;
    /// the outcome says which session it used, or why it couldn't.
    /// </summary>
    /// <param name="automation">The automation to execute.</param>
    /// <param name="eventType">Optional event type that triggered this execution.</param>
    /// <param name="eventSummary">Optional event summary providing context.</param>
    /// <param name="previousSessionId">The session the automation's last run started, for the "same_session" target.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<AutomationExecutionOutcome> ExecuteAsync(
        Automation automation,
        string? eventType = null,
        string? eventSummary = null,
        string? previousSessionId = null,
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
            return (automation.TargetType ?? "new_session") switch
            {
                "same_session" => await ExecuteOnSameSessionAsync(automation, finalPrompt, eventType, previousSessionId, ct),
                "most_recent_session" => await ExecuteOnMostRecentSessionAsync(automation, finalPrompt, ct),
                "tagged_session" => await ExecuteOnTaggedSessionAsync(automation, finalPrompt, ct),
                AutomationTargets.Workflow => await ExecuteWorkflowAsync(automation, finalPrompt, ct),
                _ => await ExecuteOnNewSessionAsync(automation, finalPrompt, eventType, ct),
            };
        }
        catch (Exception ex)
        {
            LogExecutionException(ex, automation.Id, automation.Name);
            return AutomationExecutionOutcome.Failed($"Couldn't start: {ex.Message}");
        }
    }

    /// <summary>
    /// Starts the automation's workflow with the prompt as the run's request. A start the workflow refuses (it's off, the
    /// file has errors, a model or skill isn't there) is a skipped run with the reason, never a silent one.
    /// </summary>
    private async Task<AutomationExecutionOutcome> ExecuteWorkflowAsync(Automation automation, string finalPrompt, CancellationToken ct)
    {
        if (workflows is null)
            return AutomationExecutionOutcome.SkippedBecause($"Skipped: {AutomationWorkflows.TurnedOffReason}");

        var started = await workflows.StartAsync(automation, finalPrompt, ct);
        if (started.RunId is null)
        {
            LogExecutionFailed(automation.Id, automation.Name, "Workflow.Skipped", started.SkipReason ?? "");
            return AutomationExecutionOutcome.SkippedBecause(started.SkipReason ?? "Skipped.");
        }

        LogWorkflowStarted(automation.Id, automation.Name, started.RunId);
        return new AutomationExecutionOutcome(started.FirstSessionId, null, null, started.RunId);
    }

    /// <summary>
    /// Executes automation on a new session.
    /// </summary>
    private async Task<AutomationExecutionOutcome> ExecuteOnNewSessionAsync(
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
            // Null is the default harness at run time; an automation with an agent or model keeps theirs.
            HarnessType = automation.HarnessType,
            // Where it runs comes from the automation source (AutomationSessionSourceProvider).
            Source = BuildSessionSource(automation, eventType),
            SourceReference = $"automation:{automation.Id}",
            Agent = automation.Agent,
            ProviderId = SplitModel(automation.Model).ProviderId,
            ModelId = SplitModel(automation.Model).ModelId,
        };

        var result = await sessionOrchestrator.CreateSessionAsync(request, ct);

        if (result.IsSuccess)
        {
            LogExecutionSucceeded(automation.Id, automation.Name, result.Value.Session.Id);
            return new AutomationExecutionOutcome(result.Value.Session.Id, result.Value.InstanceId, null);
        }

        LogExecutionFailed(automation.Id, automation.Name, result.Error.Code, result.Error.Description);
        return AutomationExecutionOutcome.Failed($"Couldn't start: {result.Error.Description}");
    }

    /// <summary>
    /// Prompts the session the automation's last run started, so the runs build on one conversation. The first run,
    /// or one whose session is gone or archived, starts a new session.
    /// </summary>
    private async Task<AutomationExecutionOutcome> ExecuteOnSameSessionAsync(
        Automation automation,
        string finalPrompt,
        string? eventType,
        string? previousSessionId,
        CancellationToken ct)
    {
        var previous = previousSessionId is null ? null : await sessionRepository.GetByIdAsync(previousSessionId);
        if (previous is null || previous.RetentionStatus == "archived")
            return await ExecuteOnNewSessionAsync(automation, finalPrompt, eventType, ct);

        return await PromptExistingSessionAsync(automation, previous, finalPrompt, ct)
            ?? await ExecuteOnNewSessionAsync(automation, finalPrompt, eventType, ct);
    }

    /// <summary>Prompts a session; null when it couldn't, so the caller can start a new one instead.</summary>
    private async Task<AutomationExecutionOutcome?> PromptExistingSessionAsync(
        Automation automation,
        Session targetSession,
        string finalPrompt,
        CancellationToken ct)
    {
        // The automation's agent and model apply to this run only, and only on the harness they were picked
        // from; otherwise the session answers with its own.
        var result = await sessionOrchestrator.PromptSessionOnceAsync(
            targetSession.Id,
            finalPrompt,
            ChoicesFor(automation, targetSession),
            ct);

        if (result.IsSuccess)
        {
            LogExecutionSucceeded(automation.Id, automation.Name, targetSession.Id);
            return new AutomationExecutionOutcome(targetSession.Id, targetSession.InstanceId, null);
        }

        LogExecutionFailed(automation.Id, automation.Name, result.Error.Code, result.Error.Description);
        return null;
    }

    private static PromptOptions? ChoicesFor(Automation automation, Session targetSession)
    {
        if (automation.HarnessType is not null
            && !string.Equals(automation.HarnessType, targetSession.HarnessType, StringComparison.Ordinal))
        {
            return null;
        }

        var (providerId, modelId) = SplitModel(automation.Model);
        return automation.Agent is null && modelId is null
            ? null
            : new PromptOptions { Agent = automation.Agent, ProviderId = providerId, ModelId = modelId };
    }

    /// <summary><c>provider/model</c> split at the first slash (model ids may have slashes of their own).</summary>
    internal static (string? ProviderId, string? ModelId) SplitModel(string? model)
    {
        var slash = model?.IndexOf('/', StringComparison.Ordinal) ?? -1;
        return slash > 0 && slash < model!.Length - 1
            ? (model[..slash], model[(slash + 1)..])
            : (null, null);
    }

    /// <summary>
    /// Executes automation on the most recent session, falling back to new session if none found.
    /// </summary>
    private async Task<AutomationExecutionOutcome> ExecuteOnMostRecentSessionAsync(
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
            return await PromptExistingSessionAsync(automation, sessions[0], finalPrompt, ct)
                ?? AutomationExecutionOutcome.Failed($"Couldn't prompt the most recent session, {sessions[0].Title}.");
        }

        LogNoSessionFoundFallingBack(automation.Id, automation.Name, "most_recent_session");
        return await ExecuteOnNewSessionAsync(automation, finalPrompt, eventType: null, ct);
    }

    /// <summary>
    /// Executes automation on the most recent session matching target tags, falling back to new session if none found.
    /// </summary>
    private async Task<AutomationExecutionOutcome> ExecuteOnTaggedSessionAsync(
        Automation automation,
        string finalPrompt,
        CancellationToken ct)
    {
        if (automation.TargetTags.Count == 0)
        {
            LogNoTagsSpecifiedFallingBack(automation.Id, automation.Name);
            return await ExecuteOnNewSessionAsync(automation, finalPrompt, eventType: null, ct);
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
            return await PromptExistingSessionAsync(automation, sessions[0], finalPrompt, ct)
                ?? AutomationExecutionOutcome.Failed($"Couldn't prompt the tagged session, {sessions[0].Title}.");
        }

        LogNoSessionFoundFallingBack(automation.Id, automation.Name, $"tagged_session (tags: {string.Join(", ", automation.TargetTags)})");
        return await ExecuteOnNewSessionAsync(automation, finalPrompt, eventType: null, ct);
    }

    /// <summary>
    /// Builds a SessionSourceSelection for tracking automation provenance.
    /// </summary>
    internal static SessionSourceSelection BuildSessionSource(Automation automation, string? eventType)
    {
        // Written by hand rather than serialized, which keeps it trim-safe; the writer escapes quotes and
        // backslashes in the name, so a name like `Check "flaky" tests` still makes valid JSON.
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("automationId", automation.Id);
            writer.WriteString("automationName", automation.Name);
            writer.WriteString("trigger", eventType ?? "schedule");
            writer.WriteEndObject();
        }

        return new SessionSourceSelection
        {
            Key = new SessionSourceKey
            {
                ProviderId = "builtin.automation",
                SourceType = "automation",
                ActionId = SessionSourceActions.StartSession
            },
            Input = JsonDocument.Parse(buffer.ToArray()).RootElement.Clone()
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Automation {AutomationId} ({AutomationName}) started workflow run {RunId}")]
    private partial void LogWorkflowStarted(string automationId, string automationName, string runId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Automation execution failed: {AutomationId} ({AutomationName}), error: {ErrorCode} - {ErrorMessage}")]
    private partial void LogExecutionFailed(string automationId, string automationName, string errorCode, string errorMessage);

    [LoggerMessage(Level = LogLevel.Error, Message = "Automation execution threw exception: {AutomationId} ({AutomationName})")]
    private partial void LogExecutionException(Exception ex, string automationId, string automationName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No session found for automation {AutomationId} ({AutomationName}) with target type '{TargetType}', falling back to new session")]
    private partial void LogNoSessionFoundFallingBack(string automationId, string automationName, string targetType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No tags specified for automation {AutomationId} ({AutomationName}) with target type 'tagged_session', falling back to new session")]
    private partial void LogNoTagsSpecifiedFallingBack(string automationId, string automationName);
}

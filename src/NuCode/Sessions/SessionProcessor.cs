using System.Collections.Immutable;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NuCode.Audit;

namespace NuCode.Sessions;

/// <summary>
/// Processes a single streaming agent invocation. Observes the streaming response,
/// creates/updates message parts (text, tool calls), publishes events, and
/// determines whether the conversation loop should continue, stop, or compact.
/// </summary>
internal sealed class SessionProcessor : ISessionProcessor
{
    /// <summary>Number of consecutive identical tool calls that triggers doom loop detection.</summary>
    private const int DoomLoopThreshold = 3;

    private readonly ISessionService _sessionService;
    private readonly ILogger<SessionProcessor> _logger;
    private readonly AuditEventSubscriber _auditSubscriber;

    public SessionProcessor(
        ISessionService sessionService,
        ILogger<SessionProcessor> logger,
        AuditEventSubscriber auditSubscriber)
    {
        _sessionService = sessionService;
        _logger = logger;
        _auditSubscriber = auditSubscriber;
    }

    public async Task<ProcessResult> ProcessAsync(
        AIAgent agent,
        AssistantMessage assistantMessage,
        IEnumerable<ChatMessage> chatMessages,
        NuCodeAgentSession session,
        CancellationToken ct)
    {
        // Set the ambient session context so tools (e.g. TaskTool) can discover
        // the parent session ID without needing it as a parameter.
        SessionContext.Set(assistantMessage.SessionId);
        try
        {
            // ChatClientAgent requires a ChatClientAgentSession — create one via the agent's factory method.
            // NuCodeAgentSession (which extends AgentSession) is not compatible with ChatClientAgent.
            var agentSession = await agent.CreateSessionAsync(ct);
            var stream = agent.RunStreamingAsync(chatMessages, agentSession, cancellationToken: ct);
            return await ProcessStreamAsync(stream, assistantMessage, session, ct);
        }
        finally
        {
            SessionContext.Clear();
        }
    }

    /// <summary>
    /// Processes a stream of <see cref="AgentResponseUpdate"/> items, creating/updating message
    /// parts and determining the loop result. Exposed as <c>internal</c> so tests can supply
    /// a synthetic stream without going through <see cref="ChatClientAgent"/>.
    /// </summary>
    internal async Task<ProcessResult> ProcessStreamAsync(
        IAsyncEnumerable<AgentResponseUpdate> updates,
        AssistantMessage assistantMessage,
        NuCodeAgentSession session,
        CancellationToken ct)
    {
        var sessionId = assistantMessage.SessionId;
        var messageId = assistantMessage.Id;

        // Tracks in-flight tool calls by call ID
        var toolCalls = new Dictionary<string, ToolPart>();
        var completedToolParts = new List<ToolPart>();

        // Text accumulator
        var textBuilder = new StringBuilder();
        PartId? currentTextPartId = null;
        var textStartTime = DateTimeOffset.UtcNow;

        // Result tracking
        var needsCompaction = false;
        var blocked = false;
        MessageError? error = null;
        string? finishReason = null;

        try
        {
            session.Status = new BusySessionStatus();

            // Insert the assistant message placeholder before streaming starts,
            // so that TextPart and ToolPart foreign key constraints are satisfied.
            await _sessionService.UpsertMessageAsync(assistantMessage, ct);

            var updateCount = 0;
            await foreach (var update in updates.WithCancellation(ct))
            {
                updateCount++;
                _logger.LogInformation("NuCode stream update #{Count}: FinishReason={FinishReason}, Contents={ContentCount}",
                    updateCount, update.FinishReason?.Value, update.Contents.Count);
                ct.ThrowIfCancellationRequested();

                if (needsCompaction)
                {
                    break;
                }

                // Process finish reason
                if (update.FinishReason is not null)
                {
                    finishReason = update.FinishReason.Value.Value;
                }

                // Process each content item in the update
                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        case TextContent textContent:
                            currentTextPartId = await HandleTextDeltaAsync(
                                textContent, sessionId, messageId,
                                textBuilder, currentTextPartId, textStartTime, ct);
                            break;

                        case FunctionCallContent functionCall:
                            await HandleFunctionCallAsync(
                                functionCall, sessionId, messageId,
                                toolCalls, completedToolParts, ct);
                            break;

                        case FunctionResultContent functionResult:
                            await HandleFunctionResultAsync(
                                functionResult, sessionId, messageId,
                                toolCalls, completedToolParts, ct);
                            break;
                    }
                }
            }

            _logger.LogInformation("NuCode stream completed: {UpdateCount} updates, finishReason={FinishReason}, textLength={TextLength}",
                updateCount, finishReason, textBuilder.Length);

            // Finalize text part if one is in progress
            if (currentTextPartId is not null)
            {
                await FinalizeTextPartAsync(
                    sessionId, messageId, currentTextPartId.Value,
                    textBuilder, textStartTime, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            error = new AbortedError("Operation was cancelled.");
            _logger.LogInformation("Session {SessionId} processing cancelled", sessionId);
        }
        catch (Exception ex)
        {
            error = ClassifyError(ex);

            if (error is ContextOverflowError)
            {
                needsCompaction = true;
                _logger.LogWarning(ex, "Context overflow in session {SessionId}, triggering compaction", sessionId);
            }
            else
            {
                _logger.LogError(ex, "Error processing session {SessionId}", sessionId);
            }
        }
        finally
        {
            session.Status = new IdleSessionStatus();
        }

        // Update the assistant message with final state.
        // Use CancellationToken.None so the final upsert always completes,
        // even when the original token is already cancelled (e.g. user abort).
        var completedMessage = assistantMessage with
        {
            CompletedAt = DateTimeOffset.UtcNow,
            FinishReason = finishReason,
            Error = error,
        };
        await _sessionService.UpsertMessageAsync(completedMessage, CancellationToken.None);

        // Determine result
        if (needsCompaction)
        {
            return ProcessResult.Compact;
        }

        if (blocked || error is not null)
        {
            return ProcessResult.Stop;
        }

        // If finish reason indicates tool calls, the loop should continue
        if (finishReason is "tool_calls" or "function_call")
        {
            return ProcessResult.Continue;
        }

        // If there were completed tool calls, continue for next iteration
        if (completedToolParts.Count > 0)
        {
            return ProcessResult.Continue;
        }

        return ProcessResult.Stop;
    }

    private async Task<PartId?> HandleTextDeltaAsync(
        TextContent textContent,
        SessionId sessionId,
        MessageId messageId,
        StringBuilder textBuilder,
        PartId? currentTextPartId,
        DateTimeOffset startTime,
        CancellationToken ct)
    {
        var text = textContent.Text;
        if (string.IsNullOrEmpty(text))
        {
            return currentTextPartId;
        }

        // Create part on first delta
        if (currentTextPartId is null)
        {
            var partId = PartId.New();
            currentTextPartId = partId;

            var textPart = new TextPart(
                partId, sessionId, messageId, text,
                StartTime: startTime);
            await _sessionService.UpsertPartAsync(textPart, ct);
        }

        textBuilder.Append(text);

        // Publish streaming delta
        _sessionService.PublishPartDelta(
            sessionId, messageId, currentTextPartId.Value, "text", text);

        return currentTextPartId;
    }

    private async Task FinalizeTextPartAsync(
        SessionId sessionId,
        MessageId messageId,
        PartId partId,
        StringBuilder textBuilder,
        DateTimeOffset startTime,
        CancellationToken ct)
    {
        var finalText = textBuilder.ToString().Trim();
        var textPart = new TextPart(
            partId, sessionId, messageId, finalText,
            StartTime: startTime,
            EndTime: DateTimeOffset.UtcNow);
        await _sessionService.UpsertPartAsync(textPart, ct);
    }

    private async Task HandleFunctionCallAsync(
        FunctionCallContent functionCall,
        SessionId sessionId,
        MessageId messageId,
        Dictionary<string, ToolPart> toolCalls,
        List<ToolPart> completedToolParts,
        CancellationToken ct)
    {
        var callId = functionCall.CallId;
        var toolName = functionCall.Name;
        var input = functionCall.Arguments is not null
            ? functionCall.Arguments.ToImmutableDictionary()
            : ImmutableDictionary<string, object?>.Empty;

        if (toolCalls.TryGetValue(callId, out var existing))
        {
            // Transition from pending to running
            var runningPart = existing with
            {
                State = new RunningToolCallState(input, DateTimeOffset.UtcNow, toolName),
            };
            toolCalls[callId] = runningPart;
            await _sessionService.UpsertPartAsync(runningPart, ct);

            // Doom loop detection
            await CheckDoomLoopAsync(toolName, input, completedToolParts);
        }
        else
        {
            // Create new tool part in pending state
            var partId = PartId.New();
            var rawInput = functionCall.Arguments is not null
                ? System.Text.Json.JsonSerializer.Serialize(functionCall.Arguments)
                : "{}";

            var toolPart = new ToolPart(
                partId, sessionId, messageId, callId, toolName,
                new PendingToolCallState(input, rawInput));

            toolCalls[callId] = toolPart;
            await _sessionService.UpsertPartAsync(toolPart, ct);
        }
    }

    private async Task HandleFunctionResultAsync(
        FunctionResultContent functionResult,
        SessionId sessionId,
        MessageId messageId,
        Dictionary<string, ToolPart> toolCalls,
        List<ToolPart> completedToolParts,
        CancellationToken ct)
    {
        var callId = functionResult.CallId;

        if (!toolCalls.TryGetValue(callId, out var toolPart))
        {
            _logger.LogWarning(
                "Received function result for unknown call {CallId} in session {SessionId}",
                callId, sessionId);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var startTime = toolPart.State is RunningToolCallState running
            ? running.StartTime
            : now;

        ToolPart updatedPart;

        if (functionResult.Exception is not null)
        {
            // Tool errored
            updatedPart = toolPart with
            {
                State = new ErrorToolCallState(
                    toolPart.State.Input,
                    functionResult.Exception.Message,
                    startTime,
                    now),
            };
        }
        else
        {
            // Tool completed successfully
            var output = functionResult.Result?.ToString() ?? string.Empty;
            updatedPart = toolPart with
            {
                State = new CompletedToolCallState(
                    toolPart.State.Input,
                    output,
                    toolPart.ToolName,
                    ImmutableDictionary<string, object?>.Empty,
                    startTime,
                    now),
            };
        }

        toolCalls.Remove(callId);
        completedToolParts.Add(updatedPart);
        await _sessionService.UpsertPartAsync(updatedPart, ct);
    }

    private static Task CheckDoomLoopAsync(
        string toolName,
        ImmutableDictionary<string, object?> input,
        List<ToolPart> completedToolParts)
    {
        if (completedToolParts.Count < DoomLoopThreshold)
        {
            return Task.CompletedTask;
        }

        // Check last N completed parts
        var lastN = completedToolParts.Skip(completedToolParts.Count - DoomLoopThreshold).ToList();
        var allSame = lastN.All(p =>
            p.ToolName == toolName &&
            p.State.Status != ToolCallStatus.Pending &&
            InputsMatch(p.State.Input, input));

        if (allSame)
        {
            // For now, log a warning. Full permission-based doom loop handling
            // will be added when the permission system integrates with the processor.
            throw new InvalidOperationException(
                $"Doom loop detected: tool '{toolName}' called {DoomLoopThreshold} times with identical input.");
        }

        return Task.CompletedTask;
    }

    private static bool InputsMatch(
        ImmutableDictionary<string, object?> a,
        ImmutableDictionary<string, object?> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        foreach (var kvp in a)
        {
            if (!b.TryGetValue(kvp.Key, out var bValue))
            {
                return false;
            }

            var aStr = kvp.Value?.ToString();
            var bStr = bValue?.ToString();
            if (aStr != bStr)
            {
                return false;
            }
        }

        return true;
    }

    private static MessageError ClassifyError(Exception ex)
    {
        // Check for context overflow patterns in the exception message
        var message = ex.Message;

        if (message.Contains("context_length_exceeded", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("context window", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("maximum context length", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("token limit", StringComparison.OrdinalIgnoreCase))
        {
            return new ContextOverflowError(message);
        }

        if (message.Contains("output length", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("max_tokens", StringComparison.OrdinalIgnoreCase))
        {
            return new OutputLengthError();
        }

        if (message.Contains("authentication", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("api_key", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            return new ProviderAuthError("unknown", message);
        }

        // Check for HTTP status codes suggesting retryable errors
        if (message.Contains("429", StringComparison.Ordinal) ||
            message.Contains("rate_limit", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("overloaded", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("503", StringComparison.Ordinal))
        {
            return new ApiError(message, IsRetryable: true);
        }

        if (message.Contains("500", StringComparison.Ordinal) ||
            message.Contains("502", StringComparison.Ordinal) ||
            message.Contains("504", StringComparison.Ordinal))
        {
            return new ApiError(message);
        }

        return new UnknownMessageError(message);
    }
}

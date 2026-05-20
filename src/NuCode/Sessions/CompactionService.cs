using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NuCode.Agents;
using NuCode.Configuration;
using NuCode.Plugins;

namespace NuCode.Sessions;

/// <summary>
/// Detects when conversation compaction is needed and performs it by
/// summarizing older messages via the built-in compaction agent.
/// </summary>
internal sealed class CompactionService : ICompactionService
{
    /// <summary>Default message count that triggers compaction when no config is set.</summary>
    private const int DefaultMessageThreshold = 50;

    /// <summary>Default number of recent messages preserved after compaction.</summary>
    private const int DefaultRecentMessagesToKeep = 10;

    /// <summary>Rough chars-per-token estimate for token threshold checks.</summary>
    private const int CharsPerToken = 4;

    private readonly ISessionService _sessionService;
    private readonly IPluginRegistry _pluginRegistry;
    private readonly IChatClient _chatClient;
    private readonly CompactionConfig _config;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CompactionService> _logger;

    public CompactionService(
        ISessionService sessionService,
        IPluginRegistry pluginRegistry,
        IChatClient chatClient,
        CompactionConfig? config,
        TimeProvider timeProvider,
        ILogger<CompactionService> logger)
    {
        _sessionService = sessionService;
        _pluginRegistry = pluginRegistry;
        _chatClient = chatClient;
        _config = config ?? new CompactionConfig();
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<bool> NeedsCompactionAsync(SessionId sessionId, CancellationToken ct)
    {
        if (_config.Auto == false)
        {
            return false;
        }

        var messages = await _sessionService.GetMessagesAsync(sessionId, ct);
        var messageCount = messages.Count;
        var threshold = _config.MessageThreshold ?? DefaultMessageThreshold;

        if (messageCount >= threshold)
        {
            return true;
        }

        // Check token threshold if configured
        if (_config.TokenThreshold is > 0)
        {
            var estimatedTokens = EstimateTokens(messages);
            if (estimatedTokens >= _config.TokenThreshold.Value)
            {
                return true;
            }
        }

        return false;
    }

    public async Task CompactAsync(SessionId sessionId, bool overflow, CancellationToken ct)
    {
        // Concurrency guard: skip if another compaction is already in progress
        var currentSession = await _sessionService.GetSessionAsync(sessionId, ct);
        if (currentSession?.CompactingAt is not null)
        {
            _logger.LogWarning(
                "Compaction already in progress for session {SessionId}; skipping", sessionId);
            return;
        }

        _logger.LogInformation(
            "Starting compaction for session {SessionId} (overflow: {Overflow})",
            sessionId, overflow);

        // Mark session as compacting
        await _sessionService.SetCompactingAsync(sessionId, ct);

        try
        {
            var messages = await _sessionService.GetMessagesAsync(sessionId, ct);

            if (messages.Count == 0)
            {
                _logger.LogDebug("No messages to compact in session {SessionId}", sessionId);
                return;
            }

            var recentToKeep = _config.RecentMessagesToKeep ?? DefaultRecentMessagesToKeep;
            var splitIndex = Math.Max(0, messages.Count - recentToKeep);

            if (splitIndex <= 0)
            {
                _logger.LogDebug(
                    "Not enough messages to compact in session {SessionId} (count: {Count}, keep: {Keep})",
                    sessionId, messages.Count, recentToKeep);
                return;
            }

            var messagesToCompact = messages.Take(splitIndex).ToList();

            // Fire BeforeCompaction hook
            var hookInput = new BeforeCompactionInput
            {
                SessionId = sessionId.Value,
                MessageCount = messages.Count,
                Overflow = overflow,
            };
            var hookOutput = new BeforeCompactionOutput
            {
                MessageIndicesToCompact = Enumerable.Range(0, splitIndex).ToList(),
            };

            hookOutput = await _pluginRegistry.TriggerAsync(
                BuiltInHooks.BeforeCompaction, hookInput, hookOutput);

            if (hookOutput.Cancel)
            {
                _logger.LogInformation("Compaction cancelled by hook for session {SessionId}", sessionId);
                return;
            }

            // Build transcript from messages to compact (using hook-filtered indices)
            var indicesToCompact = hookOutput.MessageIndicesToCompact
                .Where(i => i >= 0 && i < splitIndex)
                .OrderBy(i => i)
                .ToList();

            if (indicesToCompact.Count == 0)
            {
                _logger.LogDebug("No messages remaining after hook filtering for session {SessionId}", sessionId);
                return;
            }

            var transcript = BuildTranscript(indicesToCompact.Select(i => messages[i]));

            // Generate summary using the compaction agent's system prompt
            var summaryText = await GenerateSummaryAsync(transcript, ct);

            // Delete or mark old messages
            var compactedAt = _timeProvider.GetUtcNow();
            if (_config.Prune == true)
            {
                foreach (var idx in indicesToCompact)
                {
                    var msg = messages[idx].Message;
                    await _sessionService.DeleteMessageAsync(sessionId, msg.Id, ct);
                }
            }
            else
            {
                // Soft-mark: set CompactedTime on completed tool parts so downstream code skips them
                foreach (var idx in indicesToCompact)
                {
                    foreach (var part in messages[idx].Parts)
                    {
                        if (part is ToolPart { State: CompletedToolCallState completed } tool)
                        {
                            var markedState = completed with { CompactedTime = compactedAt };
                            var markedPart = tool with { State = markedState };
                            await _sessionService.UpsertPartAsync(markedPart, ct);
                        }
                    }
                }
            }

            // Insert compaction boundary and summary as a synthetic user message
            var summaryMessageId = MessageId.New();
            var summaryMessage = new UserMessage(
                summaryMessageId,
                sessionId,
                _timeProvider.GetUtcNow(),
                Agent: "compaction",
                SystemPrompt: null);
            await _sessionService.UpsertMessageAsync(summaryMessage, ct);

            // Add summary text part (marked as synthetic)
            var textPart = new TextPart(
                PartId.New(),
                sessionId,
                summaryMessageId,
                $"[Conversation Summary]\n\n{summaryText}",
                Synthetic: true);
            await _sessionService.UpsertPartAsync(textPart, ct);

            // Add compaction boundary marker
            var compactionPart = new CompactionPart(
                PartId.New(),
                sessionId,
                summaryMessageId,
                Auto: _config.Auto != false,
                Overflow: overflow);
            await _sessionService.UpsertPartAsync(compactionPart, ct);

            _logger.LogInformation(
                "Compaction completed for session {SessionId}: {CompactedCount} messages summarized, {KeptCount} kept",
                sessionId, indicesToCompact.Count, messages.Count - splitIndex);
        }
        finally
        {
            // Always clear compacting state
            await _sessionService.ClearCompactingAsync(sessionId, CancellationToken.None);
        }
    }

    private async Task<string> GenerateSummaryAsync(string transcript, CancellationToken ct)
    {
        var chatMessages = new List<ChatMessage>
        {
            new(ChatRole.System, BuiltInAgents.CompactionSystemPrompt),
            new(ChatRole.User, $"Please summarize the following conversation:\n\n{transcript}"),
        };

        var response = await _chatClient.GetResponseAsync(chatMessages, cancellationToken: ct);

        return response.Text ?? string.Empty;
    }

    private static string BuildTranscript(IEnumerable<MessageWithParts> messages)
    {
        var sb = new StringBuilder();

        foreach (var mwp in messages)
        {
            var role = mwp.Message.Role == MessageRole.User ? "User" : "Assistant";
            sb.AppendLine($"--- {role} ---");

            foreach (var part in mwp.Parts)
            {
                switch (part)
                {
                    case TextPart text:
                        sb.AppendLine(text.Text);
                        break;
                    case ToolPart tool when tool.State is CompletedToolCallState:
                        sb.AppendLine($"[Tool: {tool.ToolName}] → [tool result omitted]");
                        break;
                    case ToolPart tool:
                        sb.AppendLine($"[Tool: {tool.ToolName}]");
                        break;
                }
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static int EstimateTokens(IReadOnlyList<MessageWithParts> messages)
    {
        var totalChars = 0;
        foreach (var mwp in messages)
        {
            foreach (var part in mwp.Parts)
            {
                totalChars += part switch
                {
                    TextPart text => text.Text.Length,
                    ToolPart tool when tool.State is CompletedToolCallState completed => completed.Output.Length,
                    _ => 0,
                };
            }
        }

        return totalChars / CharsPerToken;
    }

}

using System.ComponentModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NuCode.Agents;
using NuCode.Sessions;

namespace NuCode.Tools;

/// <summary>
/// Launches a sub-agent in a child session to handle a delegated task.
/// Supports resuming previous tasks via <c>task_id</c>.
/// </summary>
internal sealed class TaskTool : INuCodeTool
{
    private readonly ISessionService _sessionService;
    private readonly IAgentProfileRegistry _profileRegistry;
    private readonly INuCodeAgentFactory _agentFactory;
    private readonly IToolRegistry _toolRegistry;
    private readonly ISessionProcessor _processor;
    private readonly ICompactionService _compactionService;
    private readonly IChatClient _chatClient;
    private readonly ILogger<TaskTool> _logger;

    public TaskTool(
        ISessionService sessionService,
        IAgentProfileRegistry profileRegistry,
        INuCodeAgentFactory agentFactory,
        IToolRegistry toolRegistry,
        ISessionProcessor processor,
        ICompactionService compactionService,
        IChatClient chatClient,
        ILogger<TaskTool> logger)
    {
        _sessionService = sessionService;
        _profileRegistry = profileRegistry;
        _agentFactory = agentFactory;
        _toolRegistry = toolRegistry;
        _processor = processor;
        _compactionService = compactionService;
        _chatClient = chatClient;
        _logger = logger;
    }

    public string Name => "task";

    public string Description => BuildDescription();

    public AIFunction ToAIFunction() =>
        AIFunctionFactory.Create(ExecuteAsync, new AIFunctionFactoryOptions
        {
            Name = Name,
            Description = Description,
        });

    [Description("Launch a new agent to handle a task autonomously.")]
    internal async Task<string> ExecuteAsync(
        [Description("A short (3-5 words) description of the task")] string description,
        [Description("The task for the agent to perform")] string prompt,
        [Description("The type of specialized agent to use for this task")] string subagentType,
        [Description("Optional task_id to resume a previous task session")] string? taskId = null,
        [Description("The command that triggered this task")] string? command = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return "Error: description is required.";
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            return "Error: prompt is required.";
        }

        if (string.IsNullOrWhiteSpace(subagentType))
        {
            return "Error: subagentType is required.";
        }

        var agentProfile = _profileRegistry.Get(subagentType);
        if (agentProfile is null)
        {
            var available = string.Join(", ",
                _profileRegistry.GetAll()
                    .Where(p => p.Mode != AgentMode.Primary)
                    .Select(p => p.Name));
            return $"Error: Unknown agent type '{subagentType}'. Available sub-agents: {available}";
        }

        if (agentProfile.Mode == AgentMode.Primary)
        {
            return $"Error: Agent '{subagentType}' is a primary agent and cannot be used as a sub-agent.";
        }

        // Resume existing session or create a new child session
        NuCodeSession? session = null;

        if (!string.IsNullOrWhiteSpace(taskId))
        {
            session = await _sessionService.GetSessionAsync(
                new SessionId(taskId), cancellationToken);
        }

        if (session is null)
        {
            var title = $"{description} (@{agentProfile.Name} subagent)";
            var parentSessionId = SessionContext.Current;

            session = parentSessionId is not null
                ? await _sessionService.CreateChildSessionAsync(
                    parentSessionId.Value, ".", title, cancellationToken)
                : await _sessionService.CreateSessionAsync(
                    ".", title, cancellationToken);
        }

        // Build the tools available for this sub-agent (exclude 'task' to prevent recursion)
        var tools = _toolRegistry.GetForProfile(agentProfile)
            .Where(t => t.Name != "task")
            .Select(t => t.ToAIFunction())
            .Cast<AITool>()
            .ToList();

        // Create the agent using the sub-agent profile
        var agent = _agentFactory.CreateAgent(agentProfile, _chatClient, tools);

        // Create a user message in the child session with the prompt
        var userMsg = new UserMessage(
            MessageId.New(), session.Id, DateTimeOffset.UtcNow,
            agentProfile.Name);
        await _sessionService.UpsertMessageAsync(userMsg, cancellationToken);

        // Store the user prompt as a text part on the user message
        var userTextPart = new TextPart(
            PartId.New(), session.Id, userMsg.Id, prompt);
        await _sessionService.UpsertPartAsync(userTextPart, cancellationToken);

        // Create the assistant message that the processor will fill
        var assistantMsg = new AssistantMessage(
            MessageId.New(), session.Id, DateTimeOffset.UtcNow,
            ParentId: userMsg.Id,
            Agent: agentProfile.Name,
            ProviderId: agentProfile.ModelId ?? "default",
            ModelId: agentProfile.ModelId ?? "default");
        await _sessionService.UpsertMessageAsync(assistantMsg, cancellationToken);

        // Build chat messages from the prompt
        var chatMessages = new List<ChatMessage>
        {
            new(ChatRole.User, prompt),
        };

        // Process the agent response in a loop, checking for proactive compaction
        var agentSession = new NuCodeAgentSession(session);
        var result = await _processor.ProcessAsync(
            agent, assistantMsg, chatMessages, agentSession, cancellationToken);

        while (result == ProcessResult.Continue)
        {
            // Check if proactive compaction is needed before the next turn
            if (await _compactionService.NeedsCompactionAsync(session.Id, cancellationToken))
            {
                _logger.LogInformation(
                    "Proactive compaction triggered for session {SessionId}", session.Id);
                await _compactionService.CompactAsync(session.Id, overflow: false, cancellationToken);
                chatMessages = await RebuildChatMessagesAsync(session.Id, cancellationToken);
            }

            result = await _processor.ProcessAsync(
                agent, assistantMsg, chatMessages, agentSession, cancellationToken);
        }

        // If context overflowed, compact and retry once
        if (result == ProcessResult.Compact)
        {
            await _compactionService.CompactAsync(session.Id, overflow: true, cancellationToken);

            var retryChatMessages = await RebuildChatMessagesAsync(session.Id, cancellationToken);

            var retryAssistantMsg = new AssistantMessage(
                MessageId.New(), session.Id, DateTimeOffset.UtcNow,
                ParentId: assistantMsg.Id,
                Agent: agentProfile.Name,
                ProviderId: agentProfile.ModelId ?? "default",
                ModelId: agentProfile.ModelId ?? "default");
            await _sessionService.UpsertMessageAsync(retryAssistantMsg, cancellationToken);

            var retryResult = await _processor.ProcessAsync(
                agent, retryAssistantMsg, retryChatMessages, agentSession, cancellationToken);

            if (retryResult == ProcessResult.Compact)
            {
                _logger.LogWarning(
                    "Retry after compaction also returned Compact for session {SessionId}; stopping to avoid infinite loop",
                    session.Id);
            }

            assistantMsg = retryAssistantMsg;
        }

        // Retrieve the final text from the assistant's message parts
        var messages = await _sessionService.GetMessagesAsync(session.Id, cancellationToken);
        var finalParts = messages
            .FirstOrDefault(m => m.Message.Id == assistantMsg.Id)?
            .Parts ?? [];

        var lastText = finalParts
            .OfType<TextPart>()
            .LastOrDefault()?.Text ?? "";

        return string.Join('\n',
            $"task_id: {session.Id} (for resuming to continue this task if needed)",
            "",
            "<task_result>",
            lastText,
            "</task_result>");
    }

    private async Task<List<ChatMessage>> RebuildChatMessagesAsync(
        SessionId sessionId, CancellationToken ct)
    {
        var compactedMessages = await _sessionService.GetMessagesAsync(sessionId, ct);
        var result = new List<ChatMessage>();
        foreach (var mwp in compactedMessages)
        {
            var role = mwp.Message.Role == MessageRole.User ? ChatRole.User : ChatRole.Assistant;
            var text = string.Join('\n', mwp.Parts.OfType<TextPart>().Select(p => p.Text));
            if (!string.IsNullOrWhiteSpace(text))
            {
                result.Add(new ChatMessage(role, text));
            }
        }

        return result;
    }

    private string BuildDescription()
    {
        var subAgents = _profileRegistry.GetAll()
            .Where(p => p.Mode != AgentMode.Primary)
            .OrderBy(p => p.Name, StringComparer.Ordinal);

        var agentList = string.Join('\n',
            subAgents.Select(a =>
                $"- {a.Name}: {a.Description ?? "This subagent should only be called manually by the user."}"));

        return $"""
            Launch a new agent to handle complex, multistep tasks autonomously.

            Available agent types:
            {agentList}

            When to use this tool:
            - When a task requires specialized expertise from a specific agent type
            - When you want to delegate work to run independently

            Parameters:
            - description: A short (3-5 words) description of the task
            - prompt: Detailed task instructions for the agent
            - subagentType: The agent type to use (from the list above)
            - taskId: Optional - pass a prior task_id to resume a previous session
            """;
    }
}

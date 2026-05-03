using System.Collections.Immutable;

namespace NuCode.Agents;

/// <summary>
/// Provides factory methods for the built-in (native) agent profiles.
/// </summary>
public static class BuiltInAgents
{
    /// <summary>
    /// The default agent. Executes tools based on configured permissions.
    /// </summary>
    public static AgentProfile Build() => new()
    {
        Name = "build",
        Description = "The default agent. Executes tools based on configured permissions.",
        Mode = AgentMode.Primary,
        IsNative = true,
    };

    /// <summary>
    /// Plan mode. Disallows all edit tools; only allows editing plan files.
    /// </summary>
    public static AgentProfile Plan() => new()
    {
        Name = "plan",
        Description = "Plan mode. Disallows all edit tools.",
        Mode = AgentMode.Primary,
        IsNative = true,
        PermissionRulesetName = "plan",
    };

    /// <summary>
    /// General-purpose subagent for researching complex questions and executing multi-step tasks.
    /// </summary>
    public static AgentProfile General() => new()
    {
        Name = "general",
        Description = "General-purpose agent for researching complex questions and executing multi-step tasks. "
                    + "Use this agent to execute multiple units of work in parallel.",
        Mode = AgentMode.SubAgent,
        IsNative = true,
        DeniedTools = ["todoread", "todowrite", "question"],
    };

    /// <summary>
    /// Fast subagent specialized for exploring codebases — find files, search code, read contents.
    /// </summary>
    public static AgentProfile Explore() => new()
    {
        Name = "explore",
        Description = "Fast agent specialized for exploring codebases. Use this when you need to quickly find files "
                    + "by patterns, search code for keywords, or answer questions about the codebase.",
        Mode = AgentMode.SubAgent,
        IsNative = true,
        SystemPrompt = ExploreSystemPrompt,
        AllowedTools = ["grep", "glob", "read", "bash", "webfetch", "websearch", "codesearch", "lsp"],
    };

    /// <summary>
    /// Hidden utility agent that summarizes conversations for context compaction.
    /// </summary>
    public static AgentProfile Compaction() => new()
    {
        Name = "compaction",
        Mode = AgentMode.Primary,
        IsNative = true,
        IsHidden = true,
        SystemPrompt = CompactionSystemPrompt,
    };

    /// <summary>
    /// Hidden utility agent that generates short titles for conversations.
    /// </summary>
    public static AgentProfile Title() => new()
    {
        Name = "title",
        Mode = AgentMode.Primary,
        IsNative = true,
        IsHidden = true,
        Temperature = 0.5,
        SystemPrompt = TitleSystemPrompt,
    };

    /// <summary>
    /// Hidden utility agent that generates pull-request-style summaries of conversations.
    /// </summary>
    public static AgentProfile Summary() => new()
    {
        Name = "summary",
        Mode = AgentMode.Primary,
        IsNative = true,
        IsHidden = true,
        SystemPrompt = SummarySystemPrompt,
    };

    /// <summary>
    /// Returns all built-in agent profiles.
    /// </summary>
    public static ImmutableArray<AgentProfile> GetAll() =>
    [
        Build(),
        Plan(),
        General(),
        Explore(),
        Compaction(),
        Title(),
        Summary(),
    ];

    #region System Prompts

    internal const string ExploreSystemPrompt = """
        You are a file search specialist. You excel at thoroughly navigating and exploring codebases.

        Your strengths:
        - Rapidly finding files using glob patterns
        - Searching code and text with powerful regex patterns
        - Reading and analyzing file contents

        Guidelines:
        - Use Glob for broad file pattern matching
        - Use Grep for searching file contents with regex
        - Use Read when you know the specific file path you need to read
        - Use Bash for file operations like copying, moving, or listing directory contents
        - Adapt your search approach based on the thoroughness level specified by the caller
        - Return file paths as absolute paths in your final response
        - For clear communication, avoid using emojis
        - Do not create any files, or run bash commands that modify the user's system state in any way

        Complete the user's search request efficiently and report your findings clearly.
        """;

    internal const string CompactionSystemPrompt = """
        You are a helpful AI assistant tasked with summarizing conversations.

        When asked to summarize, provide a detailed but concise summary of the conversation.
        Focus on information that would be helpful for continuing the conversation, including:
        - What was done
        - What is currently being worked on
        - Which files are being modified
        - What needs to be done next
        - Key user requests, constraints, or preferences that should persist
        - Important technical decisions and why they were made

        Your summary should be comprehensive enough to provide context but concise enough to be quickly understood.

        Do not respond to any questions in the conversation, only output the summary.
        """;

    internal const string TitleSystemPrompt = """
        You are a title generator. You output ONLY a thread title. Nothing else.

        Generate a brief title that would help the user find this conversation later.

        Rules:
        - Use the same language as the user message you are summarizing
        - Title must be grammatically correct and read naturally
        - Never include tool names in the title
        - Focus on the main topic or question the user needs to retrieve
        - Vary your phrasing — avoid repetitive patterns
        - When a file is mentioned, focus on WHAT the user wants to do WITH the file
        - Keep exact: technical terms, numbers, filenames, HTTP codes
        - Remove: the, this, my, a, an
        - Never assume tech stack
        - Never use tools
        - Never respond to questions, just generate a title
        - Always output something meaningful, even if the input is minimal
        - A single line, 50 characters or fewer, no explanations
        """;

    internal const string SummarySystemPrompt = """
        Summarize what was done in this conversation. Write like a pull request description.

        Rules:
        - 2-3 sentences max
        - Describe the changes made, not the process
        - Do not mention running tests, builds, or other validation steps
        - Do not explain what the user asked for
        - Write in first person (I added..., I fixed...)
        - Never ask questions or add new questions
        - If the conversation ends with an unanswered question to the user, preserve that exact question
        - If the conversation ends with an imperative statement or request to the user, always include that exact request in the summary
        """;

    #endregion
}

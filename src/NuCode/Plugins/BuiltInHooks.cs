namespace NuCode.Plugins;

/// <summary>
/// Defines the built-in hook points available in NuCode.
/// Plugins can register handlers for these hooks to modify behavior at specific lifecycle points.
/// </summary>
public static class BuiltInHooks
{
    /// <summary>
    /// Triggered when the system prompt is being assembled.
    /// Allows plugins to add, remove, or modify system prompt segments.
    /// </summary>
    public static NuCodeHook<SystemPromptInput, SystemPromptOutput> SystemPromptTransform { get; } = new("system.prompt.transform");

    /// <summary>
    /// Triggered when a tool is registered in the tool registry.
    /// Allows plugins to modify tool descriptions or parameters.
    /// </summary>
    public static NuCodeHook<ToolRegisteredInput, ToolRegisteredOutput> ToolRegistered { get; } = new("tool.registered");

    /// <summary>
    /// Triggered when a new session is created.
    /// Allows plugins to set session metadata or perform initialization.
    /// </summary>
    public static NuCodeHook<SessionCreatedInput, SessionCreatedOutput> SessionCreated { get; } = new("session.created");

    /// <summary>
    /// Triggered before a tool call is executed.
    /// Allows plugins to inspect or modify tool arguments.
    /// </summary>
    public static NuCodeHook<BeforeToolCallInput, BeforeToolCallOutput> BeforeToolCall { get; } = new("tool.execute.before");

    /// <summary>
    /// Triggered after a tool call completes.
    /// Allows plugins to inspect or modify tool output.
    /// </summary>
    public static NuCodeHook<AfterToolCallInput, AfterToolCallOutput> AfterToolCall { get; } = new("tool.execute.after");

    /// <summary>
    /// Triggered when LLM call parameters are being assembled.
    /// Allows plugins to modify temperature, topP, model, etc.
    /// </summary>
    public static NuCodeHook<ChatParamsInput, ChatParamsOutput> ChatParams { get; } = new("chat.params");

    /// <summary>
    /// Triggered before conversation compaction begins.
    /// Allows plugins to modify which messages are compacted or cancel compaction entirely.
    /// </summary>
    public static NuCodeHook<BeforeCompactionInput, BeforeCompactionOutput> BeforeCompaction { get; } = new("compaction.before");
}

// --- Hook input/output types ---

/// <summary>
/// Input for the system prompt transform hook.
/// </summary>
public sealed class SystemPromptInput
{
    /// <summary>
    /// The session ID (if available).
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// The model being used.
    /// </summary>
    public string? Model { get; init; }
}

/// <summary>
/// Mutable output for the system prompt transform hook.
/// </summary>
public sealed class SystemPromptOutput
{
    /// <summary>
    /// The system prompt segments. Plugins can add, remove, or modify entries.
    /// </summary>
    public List<string> Segments { get; set; } = [];
}

/// <summary>
/// Input for the tool registered hook.
/// </summary>
public sealed class ToolRegisteredInput
{
    /// <summary>
    /// The name of the tool being registered.
    /// </summary>
    public required string ToolName { get; init; }
}

/// <summary>
/// Mutable output for the tool registered hook.
/// </summary>
public sealed class ToolRegisteredOutput
{
    /// <summary>
    /// The tool description. Plugins can modify this.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// JSON schema string for tool parameters. Plugins can modify this.
    /// </summary>
    public string? ParametersSchema { get; set; }
}

/// <summary>
/// Input for the session created hook.
/// </summary>
public sealed class SessionCreatedInput
{
    /// <summary>
    /// The ID of the newly created session.
    /// </summary>
    public required string SessionId { get; init; }
}

/// <summary>
/// Mutable output for the session created hook.
/// </summary>
public sealed class SessionCreatedOutput
{
    /// <summary>
    /// Additional metadata to attach to the session. Plugins can add key-value pairs.
    /// </summary>
    public Dictionary<string, string> Metadata { get; set; } = [];
}

/// <summary>
/// Input for the before tool call hook.
/// </summary>
public sealed class BeforeToolCallInput
{
    /// <summary>
    /// The name of the tool being called.
    /// </summary>
    public required string ToolName { get; init; }

    /// <summary>
    /// The session ID.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// The tool call ID.
    /// </summary>
    public required string CallId { get; init; }
}

/// <summary>
/// Mutable output for the before tool call hook.
/// </summary>
public sealed class BeforeToolCallOutput
{
    /// <summary>
    /// The tool arguments as a dictionary. Plugins can modify arguments before execution.
    /// </summary>
    public Dictionary<string, object?> Arguments { get; set; } = [];
}

/// <summary>
/// Input for the after tool call hook.
/// </summary>
public sealed class AfterToolCallInput
{
    /// <summary>
    /// The name of the tool that was called.
    /// </summary>
    public required string ToolName { get; init; }

    /// <summary>
    /// The session ID.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// The tool call ID.
    /// </summary>
    public required string CallId { get; init; }

    /// <summary>
    /// The arguments the tool was called with.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Arguments { get; init; }
}

/// <summary>
/// Mutable output for the after tool call hook.
/// </summary>
public sealed class AfterToolCallOutput
{
    /// <summary>
    /// Display title for the tool result. Plugins can modify this.
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// The tool output text. Plugins can modify this.
    /// </summary>
    public string? Output { get; set; }

    /// <summary>
    /// Additional metadata from tool execution. Plugins can modify this.
    /// </summary>
    public Dictionary<string, string> Metadata { get; set; } = [];
}

/// <summary>
/// Input for the chat params hook.
/// </summary>
public sealed class ChatParamsInput
{
    /// <summary>
    /// The session ID.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// The agent name.
    /// </summary>
    public required string Agent { get; init; }

    /// <summary>
    /// The model being used.
    /// </summary>
    public string? Model { get; init; }
}

/// <summary>
/// Mutable output for the chat params hook.
/// </summary>
public sealed class ChatParamsOutput
{
    /// <summary>
    /// Temperature parameter. Plugins can modify this.
    /// </summary>
    public float? Temperature { get; set; }

    /// <summary>
    /// TopP parameter. Plugins can modify this.
    /// </summary>
    public float? TopP { get; set; }

    /// <summary>
    /// Additional provider-specific options. Plugins can add entries.
    /// </summary>
    public Dictionary<string, object?> Options { get; set; } = [];
}

/// <summary>
/// Input for the before compaction hook.
/// </summary>
public sealed class BeforeCompactionInput
{
    /// <summary>
    /// The session being compacted.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Total message count in the session.
    /// </summary>
    public required int MessageCount { get; init; }

    /// <summary>
    /// Whether compaction was triggered by a context overflow error.
    /// </summary>
    public required bool Overflow { get; init; }
}

/// <summary>
/// Mutable output for the before compaction hook.
/// </summary>
public sealed class BeforeCompactionOutput
{
    /// <summary>
    /// Indices of messages to compact. Plugins can remove indices to preserve specific messages.
    /// </summary>
    public List<int> MessageIndicesToCompact { get; set; } = [];

    /// <summary>
    /// Set to true to cancel compaction entirely.
    /// </summary>
    public bool Cancel { get; set; }
}

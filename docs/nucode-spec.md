# NuCode Framework — Technical Specification

**Version:** 0.1.0  
**Target:** .NET 10.0  
**Framework:** Microsoft.Agents.AI / Microsoft.Extensions.AI  
**Source:** `src/NuCode/`

NuCode is an experimental **in-process AI agent framework** that serves as an alternative harness to OpenCode and ClaudeCode within Weave Fleet. Unlike those external harnesses (which spawn child processes and communicate over HTTP/SSE), NuCode runs inside the host .NET process via dependency injection — eliminating process overhead, enabling tighter integration, and providing direct access to the event bus, session store, and tool registry.

---

## Table of Contents

1. [Public API Surface](#public-api-surface)
2. [Session Lifecycle](#session-lifecycle)
3. [Agent Loop](#agent-loop)
4. [Tool System](#tool-system)
5. [Event System](#event-system)
6. [Delegation Model](#delegation-model)
7. [Configuration System](#configuration-system)
8. [Key Types & Domain Model](#key-types--domain-model)
9. [Permission System](#permission-system)
10. [Plugin System](#plugin-system)
11. [LSP Integration](#lsp-integration)
12. [MCP Integration](#mcp-integration)

---

## Public API Surface

### Dependency Injection Registration

**File:** `NuCodeServiceCollectionExtensions.cs`

All NuCode services are registered via extension methods on `IServiceCollection`:

```csharp
// Overload 1: With configuration
services.AddNuCode(options => {
    options.WorkingDirectory = "/path/to/working/dir";
    options.McpServers.Add(new McpServerConfig { ... });
    options.Config = new NuCodeConfig { ... };
});

// Overload 2: Default options
services.AddNuCode();
```

**Registered Services:**

| Service | Lifetime | Purpose |
|---------|----------|---------|
| `IConfigLoader` | Singleton | Multi-layer config loading from files |
| `IOptionsFactory<NuCodeConfig>` | Singleton | Config instantiation with change token |
| `IOptionsChangeTokenSource<NuCodeConfig>` | Singleton | File change detection for config |
| `IAgentProfileRegistry` | Singleton | Agent profile lookup and registration |
| `INuCodeAgentFactory` | Singleton | Creates `AIAgent` from `AgentProfile` |
| `IToolRegistry` | Singleton | Tool registration and discovery |
| `INuCodeEventBus` | Scoped | Session-scoped event bus |
| `GlobalEventBus` | Singleton | Cross-session events |
| `ISessionStore` | Singleton | Session persistence (default: SQLite in-memory) |
| `ISessionService` | Scoped | Session lifecycle orchestration |
| `ISessionProcessor` | Scoped | Streaming agent iteration processor |
| `ICompactionService` | Scoped | Conversation context compaction |
| `IPluginRegistry` | Singleton | Plugin management and hook triggering |
| `IMcpManager` | Singleton | MCP server connections |
| `McpToolRegistration` | Singleton | Bridges MCP tools into tool registry |
| `ISkillProvider` | Singleton | File-based skill discovery |
| `IQuestionService` | Singleton | Deferred ask/reply for LLM questions |
| `IAuditService` | Singleton | JSONL audit logging |
| `AuditEventSubscriber` | Scoped | Wires audit events to per-session bus |

**Built-in Tools Registered:**
- `read` - Read files/directories with line-by-line output
- `write` - Write files with auto-mkdir
- `edit` - Exact string replacement with line-ending normalization
- `multiEdit` - Batch edit operations
- `glob` - Fast file pattern matching (supports `**/*.cs` patterns)
- `grep` - Regex content search with file filtering
- `bash` - Command execution with timeout and output truncation
- `todoRead` - Read TODO/task files
- `todoWrite` - Update TODO files
- `applyPatch` - Apply unified diff patches
- `webFetch` - HTTP GET with format conversion (markdown/text/html)
- `skill` - Load and query skill definitions
- `question` - Ask deferred questions to user
- `lsp` - Language Server Protocol integration (if ILspService available)
- `webSearch` - Web search (if IWebSearchProvider available)

---

### Core Interfaces

#### ISessionService

**File:** `Sessions/ISessionService.cs`

Orchestrates session lifecycle: creation, message/part management, event publishing, session metadata updates.

**Session Lifecycle Methods:**

```csharp
Task<NuCodeSession> CreateSessionAsync(string directory, string? title, CancellationToken ct);
Task<NuCodeSession> CreateChildSessionAsync(SessionId parentId, string directory, string? title, CancellationToken ct);
Task<NuCodeSession?> GetSessionAsync(SessionId id, CancellationToken ct);
Task<IReadOnlyList<NuCodeSession>> ListSessionsAsync(SessionFilter filter, CancellationToken ct);
Task<NuCodeSession> SetTitleAsync(SessionId id, string title, CancellationToken ct);
Task<NuCodeSession> ArchiveSessionAsync(SessionId id, CancellationToken ct);
Task<NuCodeSession> UnarchiveSessionAsync(SessionId id, CancellationToken ct);
Task<NuCodeSession> SetPermissionsAsync(SessionId id, PermissionRuleset ruleset, CancellationToken ct);
Task<NuCodeSession> SetSummaryAsync(SessionId id, SessionSummary summary, CancellationToken ct);
Task<NuCodeSession> SetRevertAsync(SessionId id, SessionRevert revert, CancellationToken ct);
Task<NuCodeSession> ClearRevertAsync(SessionId id, CancellationToken ct);
Task<NuCodeSession> TouchSessionAsync(SessionId id, CancellationToken ct);
Task<NuCodeSession> SetCompactingAsync(SessionId id, CancellationToken ct);
Task<NuCodeSession> ClearCompactingAsync(SessionId id, CancellationToken ct);
Task DeleteSessionAsync(SessionId id, CancellationToken ct);
```

**Message Methods:**

```csharp
Task<NuCodeMessage> UpsertMessageAsync(NuCodeMessage message, CancellationToken ct);
Task<IReadOnlyList<MessageWithParts>> GetMessagesAsync(SessionId sessionId, CancellationToken ct);
Task<IReadOnlyList<MessageWithParts>> GetMessagesAsync(SessionId sessionId, int limit, CancellationToken ct);
Task DeleteMessageAsync(SessionId sessionId, MessageId messageId, CancellationToken ct);
```

**Part Methods:**

```csharp
Task<MessagePart> UpsertPartAsync(MessagePart part, CancellationToken ct);
void PublishPartDelta(SessionId sessionId, MessageId messageId, PartId partId, string field, string delta);
Task DeletePartAsync(SessionId sessionId, MessageId messageId, PartId partId, CancellationToken ct);
```

**Events Published:**
- `SessionEvents.Created` - New session created
- `SessionEvents.Updated` - Session metadata changed
- `SessionEvents.Deleted` - Session deleted
- `MessageEvents.Updated` - Message upserted
- `MessageEvents.Removed` - Message deleted
- `MessageEvents.PartUpdated` - Part upserted
- `MessageEvents.PartDeltaReceived` - Streaming delta (ephemeral)
- `MessageEvents.PartRemoved` - Part deleted

#### ISessionProcessor

**File:** `Sessions/ISessionProcessor.cs`

Processes a single streaming agent invocation. Consumes `AgentResponseUpdate` stream from the agent, creates/updates message parts, publishes events, and determines loop continuation.

```csharp
Task<ProcessResult> ProcessAsync(
    AIAgent agent,
    AssistantMessage assistantMessage,
    IEnumerable<ChatMessage> chatMessages,
    NuCodeAgentSession session,
    CancellationToken ct);
```

**Returns:** `ProcessResult` enum: `Continue`, `Stop`, or `Compact`

#### IToolRegistry

**File:** `Tools/IToolRegistry.cs`

```csharp
void Register(INuCodeTool tool);
IReadOnlyList<INuCodeTool> GetAll();
IReadOnlyList<INuCodeTool> GetForProfile(AgentProfile profile);
INuCodeTool? Get(string id);
```

Tool filtering is performed per-agent-profile using `AllowedTools` and `DeniedTools` sets.

#### IAgentProfileRegistry

**File:** `Agents/IAgentProfileRegistry.cs`

```csharp
AgentProfile? Get(string name);  // Case-insensitive
IReadOnlyList<AgentProfile> GetAll();
IReadOnlyList<AgentProfile> GetVisible();  // Excludes hidden agents
void Register(AgentProfile profile);
bool TryOverride(string name, Func<AgentProfile, AgentProfile> overrides);
```

#### INuCodeAgentFactory

**File:** `Agents/INuCodeAgentFactory.cs`

```csharp
AIAgent CreateAgent(
    AgentProfile profile,
    IChatClient chatClient,
    IReadOnlyList<AITool> availableTools);
```

Creates `AIAgent` instances from profiles, applying:
- System prompt from profile
- Temperature/TopP overrides
- Tool filtering (AllowedTools/DeniedTools)
- Model/Provider overrides
- Timeout middleware if config available

#### INuCodeEventBus

**File:** `Events/INuCodeEventBus.cs`

```csharp
void Publish<TProperties>(
    NuCodeEventDefinition<TProperties> definition,
    TProperties properties);

IDisposable Subscribe<TProperties>(
    NuCodeEventDefinition<TProperties> definition,
    Action<NuCodeEvent<TProperties>> callback);

IDisposable SubscribeAll(Action<NuCodeEvent> callback);
```

---

## Session Lifecycle

### Creation

1. **Explicit Creation** via `ISessionService.CreateSessionAsync()`:
   - Generates new `SessionId` (ULID)
   - Creates slug (first 8 chars of ULID, lowercase)
   - Records CreatedAt, UpdatedAt timestamps
   - Sets default Title if not provided
   - Stamps library version
   - Persists to `ISessionStore`
   - **Publishes:** `SessionEvents.Created`

2. **Child Session Creation** via `ISessionService.CreateChildSessionAsync()`:
   - Same as above but sets `ParentId` to link to parent
   - Used by TaskTool for subagent delegation

### Session State

**NuCodeSession Record:**

```csharp
public sealed record NuCodeSession
{
    public SessionId Id { get; init; }
    public string Slug { get; init; }
    public string Directory { get; init; }
    public string Title { get; init; }
    public string Version { get; init; }
    public SessionId? ParentId { get; init; }
    
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? ArchivedAt { get; init; }
    public DateTimeOffset? CompactingAt { get; init; }
    
    public PermissionRuleset? Permissions { get; init; }
    public SessionSummary? Summary { get; init; }
    public string? ShareUrl { get; init; }
    public SessionRevert? Revert { get; init; }
}
```

**SessionStatus (Discriminated Union):**

```csharp
public abstract record SessionStatus(string Type);
public sealed record IdleSessionStatus() : SessionStatus("idle");
public sealed record BusySessionStatus() : SessionStatus("busy");
public sealed record RetrySessionStatus(int Attempt, string Message, DateTimeOffset NextRetryAt) : SessionStatus("retry");
```

### Message Structure

**NuCodeMessage (Discriminated Union):**

```csharp
public abstract record NuCodeMessage(
    MessageId Id,
    SessionId SessionId,
    MessageRole Role,  // User | Assistant
    DateTimeOffset CreatedAt);

public sealed record UserMessage(
    MessageId Id,
    SessionId SessionId,
    DateTimeOffset CreatedAt,
    string Agent,
    string? ProviderId = null,
    string? ModelId = null,
    string? SystemPrompt = null)
    : NuCodeMessage(...);

public sealed record AssistantMessage(
    MessageId Id,
    SessionId SessionId,
    DateTimeOffset CreatedAt,
    MessageId ParentId,  // Link to user message
    string Agent,
    string ProviderId,
    string ModelId,
    decimal Cost = 0m,
    TokenUsage? Tokens = null,
    DateTimeOffset? CompletedAt = null,
    string? FinishReason = null,
    bool IsSummary = false,
    MessageError? Error = null)
    : NuCodeMessage(...);
```

**Error Types:**

```csharp
public abstract record MessageError(string Name, string Message);
public sealed record ProviderAuthError(string ProviderId, string Message);
public sealed record OutputLengthError();
public sealed record AbortedError(string Message);
public sealed record ContextOverflowError(string Message, string? ResponseBody = null);
public sealed record ApiError(string Message, int? StatusCode = null, bool IsRetryable = false, string? ResponseBody = null);
public sealed record UnknownMessageError(string Message);
```

### Part Types

**MessagePart (Discriminated Union):**

```csharp
public abstract record MessagePart(string Type, PartId Id, SessionId SessionId, MessageId MessageId);

// Text content
public sealed record TextPart(
    PartId Id, SessionId SessionId, MessageId MessageId,
    string Text,
    bool Synthetic = false,
    bool Ignored = false,
    DateTimeOffset? StartTime = null,
    DateTimeOffset? EndTime = null) : MessagePart("text", ...);

// Chain-of-thought reasoning
public sealed record ReasoningPart(
    PartId Id, SessionId SessionId, MessageId MessageId,
    string Text,
    DateTimeOffset StartTime,
    DateTimeOffset? EndTime = null) : MessagePart("reasoning", ...);

// Tool invocation with state machine
public sealed record ToolPart(
    PartId Id, SessionId SessionId, MessageId MessageId,
    string CallId,
    string ToolName,
    ToolCallState State) : MessagePart("tool", ...);

// File attachments
public sealed record FilePart(
    PartId Id, SessionId SessionId, MessageId MessageId,
    string Mime, string Url,
    string? Filename = null) : MessagePart("file", ...);

// Snapshot/patch tracking
public sealed record SnapshotPart(...) : MessagePart("snapshot", ...);
public sealed record PatchPart(...) : MessagePart("patch", ...);

// Metadata
public sealed record AgentPart(PartId Id, SessionId SessionId, MessageId MessageId, string Name) : MessagePart("agent", ...);
public sealed record CompactionPart(PartId Id, SessionId SessionId, MessageId MessageId, bool Auto, bool Overflow = false) : MessagePart("compaction", ...);
public sealed record SubtaskPart(PartId Id, SessionId SessionId, MessageId MessageId, string Prompt, string Description, string Agent, string? Command = null) : MessagePart("subtask", ...);
public sealed record RetryPart(PartId Id, SessionId SessionId, MessageId MessageId, int Attempt, string Error, DateTimeOffset CreatedTime) : MessagePart("retry", ...);
public sealed record StepStartPart(PartId Id, SessionId SessionId, MessageId MessageId, string? Snapshot = null) : MessagePart("step-start", ...);
public sealed record StepFinishPart(PartId Id, SessionId SessionId, MessageId MessageId, string Reason, decimal Cost, TokenUsage Tokens, string? Snapshot = null) : MessagePart("step-finish", ...);
```

### Tool Call State Machine

**ToolCallState (Discriminated Union):**

```csharp
public enum ToolCallStatus { Pending, Running, Completed, Error }

public abstract record ToolCallState(ToolCallStatus Status, ImmutableDictionary<string, object?> Input);

public sealed record PendingToolCallState(
    ImmutableDictionary<string, object?> Input,
    string RawInput) : ToolCallState(ToolCallStatus.Pending, Input);

public sealed record RunningToolCallState(
    ImmutableDictionary<string, object?> Input,
    DateTimeOffset StartTime,
    string? Title = null) : ToolCallState(ToolCallStatus.Running, Input);

public sealed record CompletedToolCallState(
    ImmutableDictionary<string, object?> Input,
    string Output,
    string Title,
    ImmutableDictionary<string, object?> Metadata,
    DateTimeOffset StartTime,
    DateTimeOffset EndTime,
    DateTimeOffset? CompactedTime = null,
    ImmutableArray<FilePart>? Attachments = null) : ToolCallState(ToolCallStatus.Completed, Input);

public sealed record ErrorToolCallState(
    ImmutableDictionary<string, object?> Input,
    string Error,
    DateTimeOffset StartTime,
    DateTimeOffset EndTime) : ToolCallState(ToolCallStatus.Error, Input);
```

**State Transitions:**
- `Pending` → `Running` (first tool call streaming update)
- `Running` → `Completed` (successful result received)
- `Running` → `Error` (exception received)
- `Pending` → `Error` (error before execution started)

### Session Archival

- `ArchiveSessionAsync()` sets `ArchivedAt` timestamp
- `UnarchiveSessionAsync()` clears `ArchivedAt`
- Archived sessions are excluded from list queries by default
- **Publishes:** `SessionEvents.Updated`

### Compaction Lifecycle

1. `SetCompactingAsync()` - Sets `CompactingAt` during compaction
2. Compaction service runs `ICompactionService.CompactAsync()`
3. `ClearCompactingAsync()` - Clears `CompactingAt` after completion
4. New synthetic message added with `CompactionPart` marking boundary

---

## Agent Loop

### Core Loop Flow

**File:** `Sessions/SessionProcessor.cs`

```
SessionProcessor.ProcessAsync()
├─ Set SessionContext (ambient session ID)
├─ agent.RunStreamingAsync() → IAsyncEnumerable<AgentResponseUpdate>
├─ ProcessStreamAsync()
│  ├─ Initialize: toolCalls dict, text builder, result tracking
│  ├─ For each update in stream:
│  │  ├─ Extract FinishReason
│  │  ├─ For each content item:
│  │  │  ├─ TextContent → HandleTextDeltaAsync()
│  │  │  ├─ FunctionCallContent → HandleFunctionCallAsync()
│  │  │  └─ FunctionResultContent → HandleFunctionResultAsync()
│  │  └─ Check needsCompaction (break early if true)
│  ├─ Finalize text part if in progress
│  ├─ Handle errors / classify into error types
│  └─ Determine ProcessResult (Continue/Stop/Compact)
├─ Clear SessionContext
└─ Return ProcessResult
```

### Streaming Updates

**TextContent Processing:**
1. First delta: Create `TextPart` with `PartId`
2. Accumulate text in `StringBuilder`
3. Publish `PartDeltaReceived` event (ephemeral streaming)
4. Final: Upsert `TextPart` with `StartTime` and `EndTime`

**FunctionCallContent Processing:**
1. New call → Create `ToolPart` in `PendingToolCallState` with raw JSON input
2. Subsequent call for same `callId` → Transition to `RunningToolCallState`
   - Populate input from streaming parameters
   - Record `StartTime`
3. Publish `ToolEvents.Started`

**FunctionResultContent Processing:**
1. Look up `ToolPart` by `CallId`
2. If exception → `ErrorToolCallState` (duration from `StartTime` to now)
3. If success → `CompletedToolCallState` (with output, metadata)
4. Move to `completedToolParts` list
5. Publish `ToolEvents.Completed` or `ToolEvents.Failed`

### Doom Loop Detection

**Threshold:** 3 consecutive identical tool calls (by name and input)

```csharp
const int DoomLoopThreshold = 3;

// Check last N completed parts
var lastN = completedToolParts.Skip(completedToolParts.Count - DoomLoopThreshold).ToList();
var allSame = lastN.All(p =>
    p.ToolName == toolName &&
    p.State.Status != ToolCallStatus.Pending &&
    InputsMatch(p.State.Input, input));

if (allSame) throw InvalidOperationException("Doom loop detected...");
```

### Loop Continuation Decision

```
if needsCompaction → Compact
else if blocked || error → Stop
else if finishReason in ["tool_calls", "function_call"] → Continue
else if completedToolParts.Count > 0 → Continue
else → Stop
```

### Error Classification

Pattern matching on exception message to classify:

| Pattern | Error Type | Description |
|---------|-----------|-------------|
| `context_length_exceeded`, `context window`, `maximum context length`, `token limit` | `ContextOverflowError` | Triggers compaction |
| `output length`, `max_tokens` | `OutputLengthError` | Stop loop |
| `authentication`, `api_key`, `unauthorized` | `ProviderAuthError` | Stop loop |
| `429`, `rate_limit`, `overloaded`, `503` | `ApiError` (isRetryable=true) | Stop loop |
| `500`, `502`, `504` | `ApiError` (isRetryable=false) | Stop loop |
| (default) | `UnknownMessageError` | Stop loop |

---

## Tool System

### INuCodeTool Interface

**File:** `INuCodeTool.cs`

```csharp
public interface INuCodeTool
{
    string Name { get; }
    string Description { get; }
    AIFunction ToAIFunction();
}
```

Each tool converts to an `AIFunction` for use with Microsoft Agent Framework.

### Built-in Tool Catalog

#### Core File Tools

**ReadTool**
- **Name:** `read`
- **Purpose:** Read files/directories with intelligent formatting
- **Parameters:**
  - `filePath` (required): Absolute path
  - `offset` (optional): 1-indexed line start (default: 1)
  - `limit` (optional): Max lines to read (default: 2000)
- **Output:** XML-formatted with line numbers, truncated if too large
- **Features:**
  - Detects binary files by extension (`.dll`, `.exe`, etc.)
  - Detects image files (`.png`, `.jpg`, etc.)
  - Directory listing with slash suffix for subdirs
  - Line length truncation at 2000 chars

**WriteTool**
- **Name:** `write`
- **Purpose:** Write file content, creating parent directories
- **Parameters:**
  - `filePath` (required): Absolute path
  - `content` (required): File content
- **Output:** Success message indicating if file is new

**EditTool**
- **Name:** `edit`
- **Purpose:** Exact string replacement with automatic line-ending normalization
- **Parameters:**
  - `filePath` (required): Absolute path
  - `oldString` (required): Text to find
  - `newString` (required): Replacement text
  - `replaceAll` (optional): Replace all occurrences (default: false)
- **Features:**
  - Auto-detects file line endings (CRLF vs LF)
  - Normalizes search/replace strings to match file endings
  - Reports number of matches found
  - Errors if match not found or ambiguous

**MultiEditTool**
- **Name:** `multiEdit`
- **Purpose:** Batch multiple edits in one tool call
- **Implementation:** Chains multiple `EditTool` invocations

#### Search & Discovery Tools

**GlobTool**
- **Name:** `glob`
- **Purpose:** Fast file pattern matching using glob syntax
- **Parameters:**
  - `pattern` (required): Glob pattern (e.g., `**/*.cs`, `src/**/*.ts`)
  - `path` (optional): Search directory (default: cwd)
- **Output:** Sorted list of matching file paths
- **Features:**
  - Uses Microsoft.Extensions.FileSystemGlobbing
  - Returns max 1000 results
  - Sorted by modification time (descending)
  - Filters to existing files only

**GrepTool**
- **Name:** `grep`
- **Purpose:** Regex content search across files
- **Parameters:**
  - `pattern` (required): Regex pattern to search for
  - `path` (optional): Search directory
  - `include` (optional): File filter pattern (default: all files)
- **Output:** File paths with matching line numbers

#### Command Execution

**BashTool**
- **Name:** `bash`
- **Purpose:** Execute shell commands with output capture
- **Parameters:**
  - `command` (required): Shell command to execute
  - `description` (required): 5-10 word description of what command does
  - `timeout` (optional): Timeout in ms (default: 120,000)
  - `workdir` (optional): Working directory for execution
- **Platform:** Auto-detects Windows (cmd.exe) vs Unix (/bin/bash)
- **Output:**
  - Stdout/stderr combined
  - Truncated to max 2000 lines or 50KB
  - Written to temp file if output too large (returns reference)
- **Features:**
  - Persistent shell session per tool invocation
  - Timeout enforcement via `Process.WaitForExitAsync()`
  - Error handling for non-existent workdir

#### Semantic Tools

**TaskTool** (Subagent Delegation)
- **Name:** `task`
- **Purpose:** Delegate work to a sub-agent in a child session
- **Parameters:**
  - `description` (required): 3-5 word task description
  - `prompt` (required): Full task prompt for subagent
  - `subagentType` (required): Name of agent profile (e.g., "build", "explore")
  - `taskId` (optional): Resume existing task session
  - `command` (optional): Triggering command
- **Implementation:** See [Delegation Model](#delegation-model)

#### Web & External

**WebFetchTool**
- **Name:** `webFetch`
- **Purpose:** HTTP GET with format conversion
- **Parameters:**
  - `url` (required): Full URL to fetch
  - `format` (optional): Output format - `markdown`, `text`, or `html` (default: markdown)
- **Output:** Formatted content

**WebSearchTool** (Optional)
- **Name:** `webSearch`
- **Purpose:** Web search via provider
- **Availability:** Only registered if `IWebSearchProvider` available

#### Special Tools

**TodoReadTool**
- **Name:** `todoRead`
- **Purpose:** Read TODO/task tracking files (e.g., `.todo`, `todo.txt`)

**TodoWriteTool**
- **Name:** `todoWrite`
- **Purpose:** Update TODO files

**ApplyPatchTool**
- **Name:** `applyPatch`
- **Purpose:** Apply unified diff patches

**SkillTool**
- **Name:** `skill`
- **Purpose:** Load and query skill definitions from config/`.nucode/skills/`
- **Dependency:** `ISkillProvider`

**QuestionTool**
- **Name:** `question`
- **Purpose:** Ask deferred questions to user with async reply
- **Dependency:** `IQuestionService`

**LspTool**
- **Name:** `lsp`
- **Purpose:** Language Server Protocol integration
- **Dependency:** `ILspService` (optional, degrades gracefully)
- **Supports:** Symbol navigation, hover, code actions, completions, etc.

### Tool Result Format

**ToolResult Record:**

```csharp
public sealed record ToolResult(
    string Title,
    string Output,
    IReadOnlyDictionary<string, object>? Metadata = null,
    IReadOnlyList<ToolAttachment>? Attachments = null);

public sealed record ToolAttachment(
    string Name,
    string MimeType,
    ReadOnlyMemory<byte> Data);
```

### Tool Registry

**File:** `Tools/IToolRegistry.cs`

```csharp
public interface IToolRegistry
{
    void Register(INuCodeTool tool);
    IReadOnlyList<INuCodeTool> GetAll();
    IReadOnlyList<INuCodeTool> GetForProfile(AgentProfile profile);
    INuCodeTool? Get(string id);
}
```

**Tool Filtering Logic:**

```csharp
// Profile defines AllowedTools (whitelist) and DeniedTools (blacklist)
public IReadOnlyList<INuCodeTool> GetForProfile(AgentProfile profile)
{
    var tools = _tools.Values.AsEnumerable();
    
    if (profile.AllowedTools is not null)
        tools = tools.Where(t => profile.AllowedTools.Contains(t.Name));
    
    if (profile.DeniedTools is not null)
        tools = tools.Where(t => !profile.DeniedTools.Contains(t.Name));
    
    return tools.ToList().AsReadOnly();
}
```

---

## Event System

### Event Architecture

**File:** `Events/INuCodeEventBus.cs`

Two-tier event system:
1. **Scoped Event Bus** (`INuCodeEventBus`) - Per-session events
2. **Global Event Bus** (`GlobalEventBus`) - Cross-session events

**Subscription Model:**

```csharp
// Typed subscriptions
IDisposable sub = eventBus.Subscribe(SessionEvents.Created, @event =>
{
    Console.WriteLine($"Session {event.Properties.SessionId} created");
});

// Wildcard (all-event) subscriptions
IDisposable wildcard = eventBus.SubscribeAll(@event =>
{
    Console.WriteLine($"Event: {event.Type}");
});

// Unsubscribe on dispose
sub.Dispose();
```

### Event Definitions

**SessionEvents**

```csharp
public static class SessionEvents
{
    public sealed record SessionInfo(SessionId SessionId, string? Title);
    
    public static readonly NuCodeEventDefinition<SessionInfo> Created = new("session.created");
    public static readonly NuCodeEventDefinition<SessionInfo> Updated = new("session.updated");
    public static readonly NuCodeEventDefinition<SessionInfo> Deleted = new("session.deleted");
    
    public sealed record SessionError(SessionId? SessionId, string Error);
    public static readonly NuCodeEventDefinition<SessionError> Error = new("session.error");
}
```

**MessageEvents**

```csharp
public static class MessageEvents
{
    public sealed record MessageInfo(SessionId SessionId, MessageId MessageId);
    public static readonly NuCodeEventDefinition<MessageInfo> Updated = new("message.updated");
    public static readonly NuCodeEventDefinition<MessageInfo> Removed = new("message.removed");
    
    public sealed record PartInfo(SessionId SessionId, MessageId MessageId, PartId PartId);
    public static readonly NuCodeEventDefinition<PartInfo> PartUpdated = new("message.part.updated");
    
    public sealed record PartDelta(SessionId SessionId, MessageId MessageId, PartId PartId, string Field, string Delta);
    public static readonly NuCodeEventDefinition<PartDelta> PartDeltaReceived = new("message.part.delta");
    
    public static readonly NuCodeEventDefinition<PartInfo> PartRemoved = new("message.part.removed");
}
```

**ToolEvents**

```csharp
public static class ToolEvents
{
    public sealed record ToolStartedInfo(SessionId SessionId, MessageId MessageId, string ToolName, string? CallId);
    public static readonly NuCodeEventDefinition<ToolStartedInfo> Started = new("tool.started");
    
    public sealed record ToolCompletedInfo(SessionId SessionId, MessageId MessageId, string ToolName, string? CallId, string? Title);
    public static readonly NuCodeEventDefinition<ToolCompletedInfo> Completed = new("tool.completed");
    
    public sealed record ToolFailedInfo(SessionId SessionId, MessageId MessageId, string ToolName, string? CallId, string Error);
    public static readonly NuCodeEventDefinition<ToolFailedInfo> Failed = new("tool.failed");
}
```

### Event Bus Implementation

**File:** `Events/NuCodeEventBus.cs`

Thread-safe in-memory pub/sub:

```csharp
internal sealed class NuCodeEventBus : INuCodeEventBus
{
    private readonly ConcurrentDictionary<string, List<Action<NuCodeEvent>>> _typed;
    private readonly List<Action<NuCodeEvent>> _wildcards;
    private readonly object _lock;
    
    public void Publish<TProperties>(
        NuCodeEventDefinition<TProperties> definition,
        TProperties properties)
    {
        var evt = definition.Create(properties);
        NotifyTyped(definition.Type, evt);
        NotifyWildcards(evt);
    }
    
    // Subscriptions with automatic unsubscribe
    public IDisposable Subscribe<TProperties>(
        NuCodeEventDefinition<TProperties> definition,
        Action<NuCodeEvent<TProperties>> callback)
    {
        // ... wrapping logic
        return new Subscription(() => { /* remove */ });
    }
}
```

---

## Delegation Model

### TaskTool: Subagent Delegation

**File:** `Tools/TaskTool.cs`

Enables a parent agent to spawn a child agent in a separate session for autonomous task execution.

### Execution Flow

1. **Validation:**
   - Check subagent profile exists
   - Verify it's not a primary agent
   - Retrieve or create child session

2. **Session Management:**
   - Retrieve existing session by `taskId` (resume) or create new child session
   - Set title to `"{description} (@{agentProfile.Name} subagent)"`
   - Link to parent via `CreateChildSessionAsync(parentSessionId, ...)`

3. **Ambient Context:**
   - `SessionContext.Set(assistantMessage.SessionId)` allows TaskTool to access parent session ID
   - Used in `NuCodeAgentSession` initialization

4. **Tool Filtering:**
   - Filter tools for subagent profile using `IToolRegistry.GetForProfile()`
   - Exclude "task" tool to prevent infinite recursion

5. **Message Construction:**
   - Create `UserMessage` with prompt text
   - Store prompt as `TextPart` on user message
   - Create `AssistantMessage` ready for processor to populate

6. **Subagent Loop:**
   - Run processor loop via `SessionProcessor.ProcessAsync()`
   - Each iteration:
     - Invoke subagent with available tools
     - Handle tool calls, create message parts
     - Continue until completion or error

7. **Result Aggregation:**
   - Return summary of work done
   - Format includes final status, tool calls executed, output

### Parent-Child Relationship

```
Parent Session (user)
  ├─ UserMessage → "I need to build a feature"
  ├─ AssistantMessage → Calls TaskTool with subagent="builder"
  │
  └─ Child Session (created via CreateChildSessionAsync)
      ├─ ParentId = Parent.SessionId
      ├─ UserMessage → Full task prompt
      ├─ AssistantMessage → Builder agent responses
      └─ Messages/Parts...
```

**Query Patterns:**
- `GetSessionAsync(childSessionId)` → Retrieve child status
- `ListSessionsAsync(filter)` → Filter by `ParentId` to find children

### Resuming Tasks

- `taskId` parameter in TaskTool allows resuming existing child session
- Processor re-runs from last unconsumed state
- Useful for recovery or continuation after user intervention

---

## Configuration System

### NuCodeOptions

**File:** `NuCodeOptions.cs`

DI registration-time options:

```csharp
public sealed class NuCodeOptions
{
    public string WorkingDirectory { get; set; } = Directory.GetCurrentDirectory();
    public List<McpServerConfig> McpServers { get; } = [];
    public NuCodeConfig? Config { get; set; }  // Programmatic overrides
}
```

### NuCodeConfig

**File:** `Configuration/NuCodeConfig.cs`

Deserialized from `nucode.jsonc` config file:

```csharp
public sealed class NuCodeConfig
{
    [JsonPropertyName("agents")]
    public Dictionary<string, AgentConfigOverride>? Agents { get; init; }
    
    [JsonPropertyName("permission")]
    public PermissionConfig? Permission { get; init; }
    
    [JsonPropertyName("mcp")]
    public Dictionary<string, McpServerConfigEntry>? Mcp { get; init; }
    
    [JsonPropertyName("provider")]
    public Dictionary<string, ProviderConfig>? Provider { get; init; }
    
    [JsonPropertyName("enabledProviders")]
    public List<string>? EnabledProviders { get; init; }
    
    [JsonPropertyName("disabledProviders")]
    public List<string>? DisabledProviders { get; init; }
    
    [JsonPropertyName("model")]
    public string? Model { get; init; }
    
    [JsonPropertyName("smallModel")]
    public string? SmallModel { get; init; }
    
    [JsonPropertyName("defaultAgent")]
    public string? DefaultAgent { get; init; }
    
    [JsonPropertyName("plugin")]
    public List<string>? Plugins { get; init; }
    
    [JsonPropertyName("instructions")]
    public List<string>? Instructions { get; init; }
    
    [JsonPropertyName("snapshot")]
    public bool? Snapshot { get; init; }
    
    [JsonPropertyName("compaction")]
    public CompactionConfig? Compaction { get; init; }
    
    [JsonPropertyName("experimental")]
    public ExperimentalConfig? Experimental { get; init; }
    
    [JsonPropertyName("skills")]
    public Dictionary<string, SkillConfig>? Skills { get; init; }
    
    [JsonPropertyName("lsp")]
    public Dictionary<string, LspServerConfig>? Lsp { get; init; }
    
    [JsonPropertyName("lspAutoDetect")]
    public bool? LspAutoDetect { get; init; }
    
    [JsonPropertyName("timeout")]
    public TimeoutConfig? Timeout { get; init; }
    
    [JsonPropertyName("logLevel")]
    public string? LogLevel { get; init; }
}
```

### Config Loading Hierarchy

**Priority (highest to lowest):**

1. **Programmatic Overrides** (`NuCodeOptions.Config`)
2. **Project Config** (`.nucode/nucode.jsonc` in project root)
3. **Global Config** (`~/.nucode/nucode.jsonc`)
4. **Built-in Defaults**

**File:** `Configuration/ConfigLoader.cs`

- Uses JSONC parser (allows comments, trailing commas)
- Watches for file changes (triggers change token)
- Composes configs via merge semantics

### Config File Format Example

```jsonc
{
  "agents": {
    "builder": {
      "temperature": 0.3,
      "topP": 0.9,
      "maxSteps": 10,
      "allowedTools": ["read", "write", "edit", "bash", "glob"]
    },
    "explorer": {
      "temperature": 0.7,
      "deniedTools": ["write", "bash"]
    }
  },
  
  "permission": {
    "default": [
      { "permission": "bash", "pattern": "git *", "action": "allow" },
      { "permission": "write", "pattern": "*.md", "action": "allow" },
      { "permission": "bash", "pattern": "*", "action": "ask" }
    ]
  },
  
  "mcp": {
    "filesystem": {
      "transport": "stdio",
      "command": ["mcp-server-filesystem"]
    }
  },
  
  "model": "gpt-4o",
  "smallModel": "gpt-4o-mini",
  
  "compaction": {
    "enabled": true,
    "threshold": 50000,
    "strategy": "summarize"
  },
  
  "lspAutoDetect": true
}
```

### CompactionConfig

```csharp
public sealed class CompactionConfig
{
    public bool Enabled { get; init; } = true;
    public int Threshold { get; init; } = 50_000;  // Token count
    public string Strategy { get; init; } = "summarize";  // or "prune"
}
```

### TimeoutConfig

```csharp
public sealed class TimeoutConfig
{
    public int DefaultMs { get; init; } = 120_000;
    public Dictionary<string, int>? Tools { get; init; }  // Per-tool overrides
}
```

---

## Key Types & Domain Model

### Strongly-Typed Identifiers

**File:** `Identifiers.cs`

```csharp
public readonly record struct SessionId(string Value)
{
    public static SessionId New() => new(Ulid.NewUlid().ToString());
    public override string ToString() => Value;
    public static implicit operator string?(SessionId? value) => value?.Value;
}

public readonly record struct MessageId(string Value)
{
    public static MessageId New() => new(Ulid.NewUlid().ToString());
    // ... same pattern
}

public readonly record struct PartId(string Value)
{
    public static PartId New() => new(Ulid.NewUlid().ToString());
    // ... same pattern
}

public readonly record struct ToolId(string Value)
{
    public static ToolId New() => new(Ulid.NewUlid().ToString());
    // ... same pattern
}

public readonly record struct AgentId(string Value)
{
    public static AgentId New() => new(Ulid.NewUlid().ToString());
    // ... same pattern
}
```

All IDs use ULID (Universally Unique Lexicographically Sortable Identifiers) for:
- Global uniqueness
- Sortability by creation time
- URL-friendly encoding

### AgentProfile

**File:** `Agents/AgentProfile.cs`

```csharp
public sealed record AgentProfile
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public AgentMode Mode { get; init; } = AgentMode.Primary;
    
    public string? SystemPrompt { get; init; }
    public double? Temperature { get; init; }  // 0.0 - 2.0
    public double? TopP { get; init; }
    public int? MaxSteps { get; init; }
    
    public ImmutableHashSet<string>? AllowedTools { get; init; }
    public ImmutableHashSet<string>? DeniedTools { get; init; }
    public string? PermissionRulesetName { get; init; }
    
    public bool IsHidden { get; init; }
    public bool IsNative { get; init; }
    public string? ModelId { get; init; }
    public string? ProviderId { get; init; }
    public string? Color { get; init; }
    public ImmutableDictionary<string, object> Options { get; init; }
}

public enum AgentMode { Primary, Sub, Special }
```

### NuCodeAgentSession

**File:** `Sessions/NuCodeAgentSession.cs`

Adapter bridging NuCode sessions into Microsoft Agent Framework:

```csharp
public sealed class NuCodeAgentSession : AgentSession
{
    private NuCodeSession _session;
    private SessionStatus _status;
    
    public NuCodeSession Session { get; set; }
    public SessionStatus Status { get; set; }
}
```

Passed to `ChatClientAgent.RunStreamingAsync()` to maintain state between iterations.

### TokenUsage

```csharp
public sealed record TokenUsage(
    int Input,
    int Output,
    int Reasoning,
    CacheTokenUsage Cache,
    int? Total = null);

public sealed record CacheTokenUsage(int Read, int Write);
```

---

## Permission System

### Permission Model

**File:** `Permissions/PermissionRule.cs`

```csharp
public sealed record PermissionRule(
    string Permission,      // e.g., "bash", "edit", "write", "external_directory"
    string Pattern,         // e.g., "git *", "*.cs", "*"
    PermissionAction Action // Allow | Deny | Ask
);
```

### Permission Actions

**File:** `Permissions/PermissionAction.cs`

```csharp
public enum PermissionAction
{
    Allow,  // Approve immediately
    Deny,   // Reject immediately, throw PermissionDeniedException
    Ask,    // Create pending permission request, block until user replies
}
```

### Permission Ruleset

**File:** `Permissions/PermissionRuleset.cs`

```csharp
public sealed record PermissionRuleset
{
    public required string Name { get; init; }
    public ImmutableArray<PermissionRule> Rules { get; init; } = [];
    
    public PermissionRuleset WithRules(params PermissionRule[] additionalRules) =>
        this with { Rules = Rules.AddRange(additionalRules) };
}
```

**Evaluation:** Last-match-wins (rules are evaluated in order, last matching rule wins)

### Permission Service

**File:** `Permissions/IPermissionService.cs`

```csharp
public interface IPermissionService
{
    Task RequestPermissionAsync(
        SessionId sessionId,
        string permission,
        IReadOnlyList<string> patterns,
        IReadOnlyList<string> alwaysPatterns,
        IReadOnlyList<PermissionRuleset> rulesets,
        CancellationToken cancellationToken);
    
    void ReplyToPermission(string requestId, PermissionDecision decision);
    IReadOnlyList<PermissionRequest> GetPendingRequests();
    PermissionRuleset GetApprovedRuleset();
}
```

### Permission Flow

1. **Check:** Evaluate patterns against rulesets
   - All Allow → Return immediately
   - Any Deny → Throw `PermissionDeniedException`
   - Any Ask → Create pending request, block

2. **Ask:** User is prompted with:
   - Permission type and patterns
   - Options: Allow, Deny, Allow Always (this session)

3. **Reply:** Via `ReplyToPermission()`:
   - `Allow` → Unblock and continue
   - `Deny` → Throw exception
   - `AllowAlways` → Add rule to session ruleset

---

## Plugin System

### INuCodePlugin Interface

**File:** `Plugins/INuCodePlugin.cs`

```csharp
public interface INuCodePlugin
{
    string Name { get; }
    NuCodeHookCollection Initialize(IServiceProvider services);
}
```

### Hook Architecture

**File:** `Plugins/NuCodeHook.cs`

```csharp
public sealed class NuCodeHook<TInput, TOutput>
{
    public string Name { get; }
}

public delegate Task HookHandler<in TInput, TOutput>(TInput input, TOutput output);

public sealed class NuCodeHookCollection
{
    public NuCodeHookCollection On<TInput, TOutput>(
        NuCodeHook<TInput, TOutput> hook,
        HookHandler<TInput, TOutput> handler);
    
    internal IReadOnlyList<HookHandler<TInput, TOutput>> GetHandlers<TInput, TOutput>(
        NuCodeHook<TInput, TOutput> hook);
}
```

### Built-in Hooks

**File:** `Plugins/BuiltInHooks.cs`

| Hook | Input | Output | Purpose |
|------|-------|--------|---------|
| `SystemPromptTransform` | `SystemPromptInput` (sessionId, model) | `SystemPromptOutput` (segments list) | Modify system prompt |
| `ToolRegistered` | `ToolRegisteredInput` (toolName) | `ToolRegisteredOutput` (description, schema) | Modify tool metadata |
| `SessionCreated` | `SessionCreatedInput` (sessionId) | `SessionCreatedOutput` (metadata dict) | Add session metadata |
| `BeforeToolCall` | `BeforeToolCallInput` (toolName, sessionId, callId) | `BeforeToolCallOutput` (arguments dict) | Intercept/modify tool args |
| `AfterToolCall` | `AfterToolCallInput` (toolName, sessionId, callId, arguments) | `AfterToolCallOutput` (title, output, metadata) | Transform tool results |
| `ChatParams` | `ChatParamsInput` (sessionId, agent, model) | `ChatParamsOutput` (temperature, topP, options) | Adjust LLM parameters |
| `BeforeCompaction` | `BeforeCompactionInput` (sessionId, messageCount, overflow) | `BeforeCompactionOutput` (messageIndicesToCompact, cancel) | Control compaction |

### Plugin Registration & Execution

**File:** `Plugins/IPluginRegistry.cs`

```csharp
internal interface IPluginRegistry
{
    void Register(INuCodePlugin plugin);
    IReadOnlyList<INuCodePlugin> GetAll();
    
    Task<TOutput> TriggerAsync<TInput, TOutput>(
        NuCodeHook<TInput, TOutput> hook,
        TInput input,
        TOutput output);
}
```

**Execution:** Handlers run sequentially, mutations accumulate across plugins.

---

## LSP Integration

### ILspService Interface

**File:** `Lsp/ILspService.cs`

Abstraction for Language Server Protocol operations. Can be implemented by consumers or use built-in manager.

**Key Operations:**

| Operation | Purpose |
|-----------|---------|
| `GoToDefinitionAsync()` | Navigate to symbol definition |
| `FindReferencesAsync()` | Find all references to symbol |
| `HoverAsync()` | Get hover documentation |
| `DocumentSymbolAsync()` | Get all symbols in file |
| `WorkspaceSymbolAsync()` | Search symbols across workspace |
| `GoToImplementationAsync()` | Navigate to implementation |
| `GetDiagnosticsAsync()` | Fetch cached diagnostics |
| `NotifyDocumentChangedAsync()` | Notify server of file changes |
| `CompletionAsync()` | Get completion items |
| `CodeActionAsync()` | Get code actions for range |
| `FormattingAsync()` | Format document |
| `RenameAsync()` | Rename symbol workspace-wide |
| `SignatureHelpAsync()` | Get signature help |
| `SemanticTokensAsync()` | Get semantic token information |
| `CodeLensAsync()` | Get code lenses |
| `FoldingRangeAsync()` | Get folding ranges |
| `SelectionRangeAsync()` | Get selection ranges |
| `PrepareCallHierarchyAsync()` | Prepare call hierarchy |
| `IncomingCallsAsync()` | Get incoming calls |
| `OutgoingCallsAsync()` | Get outgoing calls |
| `GoToTypeDefinitionAsync()` | Navigate to type definition |
| `GoToDeclarationAsync()` | Navigate to declaration |
| `DocumentHighlightAsync()` | Get document highlights |
| `PrepareTypeHierarchyAsync()` | Prepare type hierarchy |
| `SupertypesAsync()` | Get supertypes |
| `SubtypesAsync()` | Get subtypes |
| `DocumentLinkAsync()` | Get document links |
| `ExecuteCommandAsync()` | Execute server command |

### LSP Tool

**Name:** `lsp`

Exposes LSP service operations as a tool for agents to invoke symbol navigation, code analysis, etc.

---

## MCP Integration

### IMcpManager Interface

**File:** `Mcp/IMcpManager.cs`

```csharp
public interface IMcpManager : IAsyncDisposable
{
    Task ConnectAllAsync(CancellationToken cancellationToken);
    Task ConnectAsync(string name, CancellationToken cancellationToken);
    Task DisconnectAsync(string name, CancellationToken cancellationToken);
    
    IReadOnlyDictionary<string, McpServerState> GetStatus();
    Task<IReadOnlyList<AITool>> GetToolsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<string, IReadOnlyList<AITool>>> GetToolsByServerAsync(CancellationToken cancellationToken);
    
    Task AddAsync(McpServerConfig config, CancellationToken cancellationToken);
    event Action<McpServerState>? ServerStateChanged;
    Task<McpServerState> CheckHealthAsync(string name, CancellationToken cancellationToken);
}
```

### McpServerConfig

**File:** `Mcp/McpServerConfig.cs`

```csharp
public sealed class McpServerConfig
{
    public required string Name { get; init; }
    public required McpTransport Transport { get; init; }  // Stdio | Http
    
    public ImmutableArray<string> Command { get; init; }
    public string? Url { get; init; }
    public ImmutableDictionary<string, string>? Environment { get; init; }
    public ImmutableDictionary<string, string>? Headers { get; init; }
    
    public bool Enabled { get; init; } = true;
    public int TimeoutMs { get; init; } = 30_000;
    public bool AutoReconnect { get; init; } = true;
    public int MaxRestarts { get; init; } = 3;
}

public enum McpTransport { Stdio, Http }
```

### MCP Tool Registration

**File:** `Mcp/McpToolRegistration.cs`

- Bridges MCP tools into the tool registry
- Tools from MCP servers become available to agents
- Registered at startup during `AddNuCodeCore()`

---

## Summary: Key Architectural Patterns

### 1. **Discriminated Unions**
- Used extensively: `NuCodeMessage`, `MessagePart`, `ToolCallState`, `SessionStatus`, `MessageError`
- Type-safe pattern matching via C# records

### 2. **Strongly-Typed IDs**
- `SessionId`, `MessageId`, `PartId`, `ToolId`, `AgentId`
- ULID-based for sortability and URL safety

### 3. **Immutable Data**
- Records for domain models
- `ImmutableDictionary`, `ImmutableArray`, `ImmutableHashSet` for collections
- Enables safe concurrent access

### 4. **Event-Driven Architecture**
- Publish/subscribe event bus with typed subscriptions
- Session-scoped and global buses
- Enables loose coupling between components

### 5. **Ambient Context (AsyncLocal)**
- `SessionContext` holds current session ID
- Allows tools to access parent session without explicit parameter passing

### 6. **Configuration Hierarchy**
- Programmatic > Project config > Global config > Built-in defaults
- JSONC format with comments support
- File change watching for hot reload

### 7. **Tool Registry with Profile Filtering**
- Tools registered globally
- Each profile defines AllowedTools (whitelist) and DeniedTools (blacklist)
- Agents only see filtered tools

### 8. **Agent Factory Pattern**
- `INuCodeAgentFactory` creates agents from profiles
- Applies profile-specific configuration (temperature, tools, permissions)
- Middleware support (e.g., timeouts)

### 9. **Streaming Response Processing**
- `SessionProcessor` consumes `AgentResponseUpdate` stream
- Creates message parts on-the-fly as updates arrive
- Events published for real-time updates (streaming deltas)

### 10. **State Machine for Tool Calls**
- `ToolCallState` tracks: Pending → Running → Completed/Error
- Enables recovery and progress tracking

### 11. **Plugin Hook System**
- Plugins register handlers for specific hooks
- Handlers receive typed input and mutable output
- Accumulating mutations across plugins

### 12. **Delegation via Child Sessions**
- TaskTool creates child sessions linked via `ParentId`
- Ambient session context allows subagent to access parent
- Enables autonomous multi-level task breakdown

---

## Configuration & Extension Points

### DI Container Extension
- Add custom tools: `services.GetRequiredService<IToolRegistry>().Register(...)`
- Add custom plugins: `services.GetRequiredService<IPluginRegistry>().Register(...)`
- Add custom agents: `services.GetRequiredService<IAgentProfileRegistry>().Register(...)`
- Override session store: Replace `ISessionStore` registration

### Agent Profile Overrides
- Via config file: `nucode.jsonc` agents section
- Programmatically: `IAgentProfileRegistry.TryOverride()`
- Applies to temperature, tools, model, permissions, etc.

### Permission Rules
- Defined in config or session-level
- Patterns use wildcard matching (e.g., `git *`, `*.cs`)
- Actions: Allow, Deny, Ask

### Hooks for Customization
- System prompt injection
- Tool metadata modification
- Tool argument/result transformation
- LLM parameter adjustment
- Compaction control

---

## End of Specification

This specification covers all major components of the NuCode framework. Each section includes:
- **File locations** for source code reference
- **API surface** with method signatures
- **Domain models** with record definitions
- **Event types** and subscription patterns
- **Execution flows** and state transitions
- **Configuration options** and customization points
- **Architectural patterns** and design principles

For implementation details, refer to the corresponding source files in `src/NuCode/`.

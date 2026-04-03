# Weave Agent Fleet: .NET 10 / C# 14 Rewrite Plan

## TL;DR
> **Summary**: Complete rewrite of the Weave Agent Fleet backend from Go to .NET 10 / C# 14, leveraging SignalR for real-time communication, EF Core for data access, and introducing observability, plugin architecture, and advanced features beyond Go parity.
> **Estimated Effort**: XL (6-8 weeks for a single developer)

## Context

### Original Request
Rewrite the Weave Agent Fleet Go backend to .NET 10 / C# 14 with ambitious new features. The existing Go implementation manages AI agent sessions via pluggable harnesses (OpenCode, Claude Code), with WebSocket-based real-time updates, SQLite persistence, and a pool manager for instance lifecycle.

### Key Findings from Go Codebase Analysis

**Architecture Summary:**
- 6 domain entities: Workspace, Instance, Session, SessionCallback, Message, MessagePart
- 2 harness implementations: OpenCode (HTTP+SSE server mode), Claude Code (subprocess NDJSON)
- Channel-based pub/sub message bus with topic subscriptions
- Manual WebSocket hub with ping/pong and topic filtering
- LRU pool manager with spawn deduplication (singleflight pattern) and health monitoring
- ~50 repository methods across 7 entity groups
- ~40 HTTP API endpoints

**Key Patterns Identified:**
1. **Harness abstraction**: `Harness` interface with `Available()`, `Start()`, `Resume()`, `Capabilities()`
2. **Session abstraction**: `Session` interface with `ID()`, `Events()`, `Status()`, `IsPending()`, `Initialize()`, `SendPrompt()`, `Abort()`, `Wait()`
3. **Event forwarding**: Harness events are rewritten (session IDs) and forwarded to bus topics
4. **Pending sessions**: Claude Code requires prompt at start; sessions can be created in pending state
5. **Instance modes**: Server mode (OpenCode) vs Subprocess mode (Claude Code)
6. **Spawn deduplication**: Concurrent spawn requests for same directory coalesce

**Known Gaps to Address:**
- No OpenTelemetry (structured logging only)
- No Unix domain socket support
- No pool recovery on startup (orphan processes)
- No event replay on WebSocket reconnect (no sequence IDs)
- Stub endpoints for diffs, config, skills, integrations
- Workspace CRUD tied to session creation

## Objectives

### Core Objective
Build a production-ready, observable, extensible .NET 10 backend that exceeds Go parity with modern C# idioms and first-class .NET features.

### Deliverables
- [ ] Complete solution with Clean Architecture layers
- [ ] Full domain model with strongly-typed IDs
- [ ] EF Core persistence with SQLite and migrations
- [ ] SignalR hub replacing manual WebSocket implementation
- [ ] System.Threading.Channels-based message bus
- [ ] OpenCode and Claude Code harness implementations
- [ ] OpenTelemetry integration (traces, metrics, logs)
- [ ] Comprehensive test suite (unit, integration)
- [ ] CLI application with System.CommandLine

### Definition of Done
- [ ] `dotnet build --configuration Release` succeeds with no warnings
- [ ] `dotnet test` passes all tests
- [ ] API endpoints match Go implementation behavior
- [ ] Frontend can connect and operate sessions
- [ ] Health checks pass (`/healthz`, `/readyz`)

### Guardrails (Must NOT)
- Do NOT use `public` for types in `*.Internal.*` namespaces
- Do NOT leave compilation warnings unfixed
- Do NOT use optional parameters (use overloads)
- Do NOT create non-sealed concrete classes without justification
- Do NOT use exceptions for expected failures (use Result pattern)

---

## Phase 1: Foundation & Skeleton

### Objective
Establish the solution structure, domain model, configuration system, and storage layer.

### TODOs

- [ ] 1.1 **Create Solution Structure**
  **What**: Initialize .NET 10 solution with Clean Architecture project layout
  **Files**:
  ```
  WeaveFleet.sln
  src/
    WeaveFleet.Domain/WeaveFleet.Domain.csproj
    WeaveFleet.Application/WeaveFleet.Application.csproj
    WeaveFleet.Infrastructure/WeaveFleet.Infrastructure.csproj
    WeaveFleet.Api/WeaveFleet.Api.csproj
    WeaveFleet.Cli/WeaveFleet.Cli.csproj
  tests/
    WeaveFleet.Domain.Tests/WeaveFleet.Domain.Tests.csproj
    WeaveFleet.Application.Tests/WeaveFleet.Application.Tests.csproj
    WeaveFleet.Infrastructure.Tests/WeaveFleet.Infrastructure.Tests.csproj
    WeaveFleet.Api.Tests/WeaveFleet.Api.Tests.csproj
  Directory.Build.props
  Directory.Packages.props
  global.json
  .editorconfig
  ```
  **Acceptance**: `dotnet build` succeeds, all projects reference correct layers

- [ ] 1.2 **Configure Central Package Management**
  **What**: Set up Directory.Packages.props with all NuGet dependencies
  **Files**: `Directory.Packages.props`
  **Packages**:
  ```xml
  <!-- Core -->
  <PackageVersion Include="Microsoft.EntityFrameworkCore" Version="10.0.0" />
  <PackageVersion Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.0.0" />
  <PackageVersion Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.0" />
  <PackageVersion Include="Microsoft.AspNetCore.SignalR" Version="10.0.0" />
  <PackageVersion Include="System.CommandLine" Version="2.0.0-beta5.*" />
  
  <!-- Observability -->
  <PackageVersion Include="OpenTelemetry" Version="1.10.0" />
  <PackageVersion Include="OpenTelemetry.Extensions.Hosting" Version="1.10.0" />
  <PackageVersion Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="1.10.0" />
  <PackageVersion Include="OpenTelemetry.Instrumentation.AspNetCore" Version="1.10.0" />
  <PackageVersion Include="OpenTelemetry.Instrumentation.Http" Version="1.10.0" />
  
  <!-- Utilities -->
  <PackageVersion Include="Polly" Version="8.5.0" />
  <PackageVersion Include="Polly.Extensions" Version="8.5.0" />
  
  <!-- Testing -->
  <PackageVersion Include="xunit" Version="2.9.0" />
  <PackageVersion Include="xunit.runner.visualstudio" Version="2.8.2" />
  <PackageVersion Include="FluentAssertions" Version="7.0.0" />
  <PackageVersion Include="NSubstitute" Version="5.3.0" />
  <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
  <PackageVersion Include="Testcontainers" Version="4.0.0" />
  ```
  **Acceptance**: All projects build with centrally managed versions

- [ ] 1.3 **Create Strongly-Typed IDs**
  **What**: Define record structs for type-safe entity identifiers
  **Files**: `src/WeaveFleet.Domain/Identifiers/`
  ```csharp
  // WorkspaceId.cs
  namespace WeaveFleet.Domain.Identifiers;
  
  public readonly record struct WorkspaceId(Guid Value)
  {
      public static WorkspaceId New() => new(Guid.NewGuid());
      public static WorkspaceId Parse(string value) => new(Guid.Parse(value));
      public override string ToString() => Value.ToString();
  }
  
  // Similarly: SessionId, InstanceId, MessageId, MessagePartId, SessionCallbackId, WorkspaceRootId
  ```
  **Acceptance**: All entity IDs are strongly typed, no raw string IDs in domain

- [ ] 1.4 **Define Domain Enums**
  **What**: Create all status and type enumerations
  **Files**: `src/WeaveFleet.Domain/Enums/`
  ```csharp
  // SessionStatus.cs
  namespace WeaveFleet.Domain.Enums;
  
  public enum SessionStatus
  {
      Active,
      Idle,
      Stopped,
      Completed,
      Disconnected,
      Error,
      WaitingInput
  }
  
  // IsolationStrategy.cs
  public enum IsolationStrategy { Existing, Worktree, Clone }
  
  // InstanceStatus.cs
  public enum InstanceStatus { Running, Stopped }
  
  // MessageRole.cs
  public enum MessageRole { User, Assistant }
  
  // MessagePartType.cs
  public enum MessagePartType { Text, ToolUse, ToolResult }
  
  // CallbackStatus.cs
  public enum CallbackStatus { Pending, Fired }
  
  // HarnessMode.cs
  public enum HarnessMode { Server, Subprocess }
  
  // HarnessSessionStatus.cs
  public enum HarnessSessionStatus { Pending, Busy, Idle, Done, Error }
  ```
  **Acceptance**: All enums defined with correct values matching Go constants

- [ ] 1.5 **Create Domain Entities**
  **What**: Define immutable entity records with proper encapsulation
  **Files**: `src/WeaveFleet.Domain/Entities/`
  ```csharp
  // Workspace.cs
  namespace WeaveFleet.Domain.Entities;
  
  public sealed class Workspace
  {
      public required WorkspaceId Id { get; init; }
      public required string Directory { get; init; }
      public string? SourceDirectory { get; init; }
      public required IsolationStrategy IsolationStrategy { get; init; }
      public string? Branch { get; init; }
      public string? DisplayName { get; set; }
      public required DateTimeOffset CreatedAt { get; init; }
      public DateTimeOffset? CleanedUpAt { get; set; }
  }
  
  // Instance.cs
  public sealed class Instance
  {
      public required InstanceId Id { get; init; }
      public required int Port { get; init; }
      public int? Pid { get; init; }
      public required string Directory { get; init; }
      public required string Url { get; init; }
      public required InstanceStatus Status { get; set; }
      public required string HarnessType { get; init; }
      public required DateTimeOffset CreatedAt { get; init; }
      public DateTimeOffset? StoppedAt { get; set; }
  }
  
  // Session.cs
  public sealed class Session
  {
      public required SessionId Id { get; init; }
      public required WorkspaceId WorkspaceId { get; init; }
      public required InstanceId InstanceId { get; init; }
      public required string HarnessSessionId { get; set; }
      public required string Title { get; set; }
      public required SessionStatus Status { get; set; }
      public required string Directory { get; init; }
      public SessionId? ParentSessionId { get; init; }
      public long TotalTokens { get; set; }
      public decimal TotalCost { get; set; }
      public required DateTimeOffset CreatedAt { get; init; }
      public DateTimeOffset? StoppedAt { get; set; }
  }
  
  // Message.cs
  public sealed class Message
  {
      public required MessageId Id { get; init; }
      public required SessionId SessionId { get; init; }
      public required MessageRole Role { get; init; }
      public required long Seq { get; init; }
      public required DateTimeOffset CreatedAt { get; init; }
  }
  
  // MessagePart.cs
  public sealed class MessagePart
  {
      public required MessagePartId Id { get; init; }
      public required MessageId MessageId { get; init; }
      public required SessionId SessionId { get; init; }
      public required MessagePartType Type { get; init; }
      public required long Seq { get; init; }
      public required string Content { get; set; } // JSON
      public required DateTimeOffset CreatedAt { get; init; }
  }
  
  // SessionCallback.cs
  public sealed class SessionCallback
  {
      public required SessionCallbackId Id { get; init; }
      public required SessionId SourceSessionId { get; init; }
      public required SessionId TargetSessionId { get; init; }
      public required InstanceId TargetInstanceId { get; init; }
      public required CallbackStatus Status { get; set; }
      public required DateTimeOffset CreatedAt { get; init; }
      public DateTimeOffset? FiredAt { get; set; }
  }
  
  // WorkspaceRoot.cs
  public sealed class WorkspaceRoot
  {
      public required WorkspaceRootId Id { get; init; }
      public required string Path { get; init; }
      public required DateTimeOffset CreatedAt { get; init; }
  }
  ```
  **Acceptance**: All 7 entity types defined with proper C# conventions

- [ ] 1.6 **Create Configuration Model**
  **What**: Define strongly-typed configuration with IOptions pattern
  **Files**: `src/WeaveFleet.Application/Configuration/`
  ```csharp
  // FleetOptions.cs
  namespace WeaveFleet.Application.Configuration;
  
  public sealed class FleetOptions
  {
      public const string SectionName = "Fleet";
      
      public int Port { get; set; } = 3000;
      public string Host { get; set; } = "127.0.0.1";
      public string DatabasePath { get; set; } = "~/.weave/fleet.db";
      public bool Debug { get; set; }
      public int MaxInstances { get; set; } = 10;
      public int IdleTimeoutSeconds { get; set; } = 300;
      public int PortRangeStart { get; set; } = 4100;
      public int PortRangeEnd { get; set; } = 4203;
  }
  
  // HarnessOptions.cs
  public sealed class HarnessOptions
  {
      public const string SectionName = "Harnesses";
      
      public OpenCodeOptions OpenCode { get; set; } = new();
      public ClaudeCodeOptions ClaudeCode { get; set; } = new();
  }
  
  public sealed class OpenCodeOptions
  {
      public string BinaryPath { get; set; } = "opencode";
      public TimeSpan SpawnTimeout { get; set; } = TimeSpan.FromSeconds(30);
      public TimeSpan HealthCheckInterval { get; set; } = TimeSpan.FromSeconds(15);
      public int MaxHealthCheckFailures { get; set; } = 3;
  }
  
  public sealed class ClaudeCodeOptions
  {
      public string BinaryPath { get; set; } = "claude";
  }
  ```
  **Acceptance**: Configuration loads from appsettings.json, env vars, and CLI

- [ ] 1.7 **Create Repository Interfaces**
  **What**: Define repository abstractions in Application layer
  **Files**: `src/WeaveFleet.Application/Persistence/`
  ```csharp
  // IFleetRepository.cs
  namespace WeaveFleet.Application.Persistence;
  
  public interface IFleetRepository
  {
      // Workspaces
      Task InsertWorkspaceAsync(Workspace workspace, CancellationToken ct = default);
      Task<Workspace?> GetWorkspaceAsync(WorkspaceId id, CancellationToken ct = default);
      Task<Workspace?> GetWorkspaceByDirectoryAsync(string directory, IsolationStrategy strategy, CancellationToken ct = default);
      Task<IReadOnlyList<Workspace>> ListWorkspacesAsync(CancellationToken ct = default);
      Task MarkWorkspaceCleanedAsync(WorkspaceId id, CancellationToken ct = default);
      Task UpdateWorkspaceDisplayNameAsync(WorkspaceId id, string displayName, CancellationToken ct = default);
      
      // Instances
      Task InsertInstanceAsync(Instance instance, CancellationToken ct = default);
      Task<Instance?> GetInstanceAsync(InstanceId id, CancellationToken ct = default);
      Task<Instance?> GetInstanceByDirectoryAsync(string directory, CancellationToken ct = default);
      Task<IReadOnlyList<Instance>> ListInstancesAsync(CancellationToken ct = default);
      Task<IReadOnlyList<Instance>> GetRunningInstancesAsync(CancellationToken ct = default);
      Task UpdateInstanceStatusAsync(InstanceId id, InstanceStatus status, CancellationToken ct = default);
      Task<int> MarkAllInstancesStoppedAsync(CancellationToken ct = default);
      
      // Sessions - approximately 20 methods
      Task InsertSessionAsync(Session session, CancellationToken ct = default);
      Task<Session?> GetSessionAsync(SessionId id, CancellationToken ct = default);
      Task<Session?> GetSessionByHarnessIdAsync(string harnessSessionId, CancellationToken ct = default);
      Task<IReadOnlyList<Session>> ListSessionsAsync(ListSessionsOptions options, CancellationToken ct = default);
      Task<long> CountSessionsAsync(IReadOnlyList<SessionStatus> statuses, CancellationToken ct = default);
      Task<SessionStatusCounts> GetSessionStatusCountsAsync(CancellationToken ct = default);
      Task<IReadOnlyList<Session>> ListActiveSessionsAsync(CancellationToken ct = default);
      Task UpdateSessionStatusAsync(SessionId id, SessionStatus status, CancellationToken ct = default);
      Task UpdateSessionTitleAsync(SessionId id, string title, CancellationToken ct = default);
      Task UpdateSessionHarnessIdAsync(SessionId id, string harnessSessionId, CancellationToken ct = default);
      Task UpdateSessionForResumeAsync(SessionId id, InstanceId instanceId, CancellationToken ct = default);
      Task<IReadOnlyList<Session>> GetSessionsForInstanceAsync(InstanceId instanceId, CancellationToken ct = default);
      Task<Session?> GetAnySessionForInstanceAsync(InstanceId instanceId, CancellationToken ct = default);
      Task<IReadOnlyList<Session>> GetNonTerminalSessionsForInstanceAsync(InstanceId instanceId, CancellationToken ct = default);
      Task<IReadOnlyList<Session>> GetActiveChildSessionsAsync(SessionId parentId, CancellationToken ct = default);
      Task<HashSet<SessionId>> GetSessionIdsWithActiveChildrenAsync(CancellationToken ct = default);
      Task<IReadOnlyList<Session>> GetSessionsForWorkspaceAsync(WorkspaceId workspaceId, CancellationToken ct = default);
      Task<bool> DeleteSessionAsync(SessionId id, CancellationToken ct = default);
      Task<int> MarkAllNonTerminalSessionsStoppedAsync(CancellationToken ct = default);
      Task<FleetTokenTotals> IncrementSessionTokensAsync(SessionId id, long tokens, decimal cost, CancellationToken ct = default);
      Task<FleetTokenTotals> GetFleetTokenTotalsAsync(CancellationToken ct = default);
      
      // Session Callbacks
      Task InsertSessionCallbackAsync(SessionCallback callback, CancellationToken ct = default);
      Task<IReadOnlyList<SessionCallback>> GetPendingCallbacksForSessionAsync(SessionId sourceSessionId, CancellationToken ct = default);
      Task<IReadOnlyList<SessionCallback>> GetAllPendingCallbacksAsync(CancellationToken ct = default);
      Task<bool> ClaimPendingCallbackAsync(SessionCallbackId id, CancellationToken ct = default);
      Task MarkCallbackFiredAsync(SessionCallbackId id, CancellationToken ct = default);
      Task<int> DeleteCallbacksForSessionAsync(SessionId sessionId, CancellationToken ct = default);
      
      // Workspace Roots
      Task InsertWorkspaceRootAsync(WorkspaceRoot root, CancellationToken ct = default);
      Task<IReadOnlyList<WorkspaceRoot>> ListWorkspaceRootsAsync(CancellationToken ct = default);
      Task<bool> DeleteWorkspaceRootAsync(WorkspaceRootId id, CancellationToken ct = default);
      Task<WorkspaceRoot?> GetWorkspaceRootByPathAsync(string path, CancellationToken ct = default);
      
      // Messages
      Task InsertMessageAsync(Message message, CancellationToken ct = default);
      Task InsertMessagePartAsync(MessagePart part, CancellationToken ct = default);
      Task<Message?> GetMessageByIdAsync(MessageId id, CancellationToken ct = default);
      Task<MessagePart?> GetMessagePartByIdAsync(MessagePartId id, CancellationToken ct = default);
      Task<IReadOnlyList<Message>> ListMessagesForSessionAsync(SessionId sessionId, CancellationToken ct = default);
      Task<IReadOnlyList<MessagePart>> ListPartsForMessageAsync(MessageId messageId, CancellationToken ct = default);
      Task<IReadOnlyList<MessageWithParts>> GetMessagesWithPartsAsync(SessionId sessionId, CancellationToken ct = default);
      Task<long> NextMessageSeqAsync(SessionId sessionId, CancellationToken ct = default);
      Task<long> NextPartSeqAsync(MessageId messageId, CancellationToken ct = default);
      Task UpdateMessagePartContentAsync(MessagePartId id, string content, CancellationToken ct = default);
      Task DeleteMessagesForSessionAsync(SessionId sessionId, CancellationToken ct = default);
  }
  
  // Supporting types
  public sealed record ListSessionsOptions(int Limit = 50, int Offset = 0, IReadOnlyList<SessionStatus>? Statuses = null);
  public sealed record SessionStatusCounts(int Active, int Idle);
  public sealed record FleetTokenTotals(long TotalTokens, decimal TotalCost);
  public sealed record MessageWithParts(Message Message, IReadOnlyList<MessagePart> Parts);
  ```
  **Acceptance**: All ~50 repository methods defined with async signatures

- [ ] 1.8 **Create EF Core DbContext**
  **What**: Implement DbContext with entity configurations
  **Files**: `src/WeaveFleet.Infrastructure/Persistence/`
  ```csharp
  // FleetDbContext.cs
  namespace WeaveFleet.Infrastructure.Persistence;
  
  internal sealed class FleetDbContext : DbContext
  {
      public FleetDbContext(DbContextOptions<FleetDbContext> options) : base(options) { }
      
      public DbSet<WorkspaceEntity> Workspaces => Set<WorkspaceEntity>();
      public DbSet<InstanceEntity> Instances => Set<InstanceEntity>();
      public DbSet<SessionEntity> Sessions => Set<SessionEntity>();
      public DbSet<SessionCallbackEntity> SessionCallbacks => Set<SessionCallbackEntity>();
      public DbSet<WorkspaceRootEntity> WorkspaceRoots => Set<WorkspaceRootEntity>();
      public DbSet<MessageEntity> Messages => Set<MessageEntity>();
      public DbSet<MessagePartEntity> MessageParts => Set<MessagePartEntity>();
      
      protected override void OnModelCreating(ModelBuilder modelBuilder)
      {
          modelBuilder.ApplyConfigurationsFromAssembly(typeof(FleetDbContext).Assembly);
      }
  }
  
  // Entities (internal DB representations)
  // WorkspaceEntity.cs, InstanceEntity.cs, SessionEntity.cs, etc.
  // Each with Id as string (stored as TEXT in SQLite)
  
  // Configurations/WorkspaceConfiguration.cs
  internal sealed class WorkspaceConfiguration : IEntityTypeConfiguration<WorkspaceEntity>
  {
      public void Configure(EntityTypeBuilder<WorkspaceEntity> builder)
      {
          builder.ToTable("workspaces");
          builder.HasKey(e => e.Id);
          builder.Property(e => e.Id).HasMaxLength(36);
          builder.Property(e => e.Directory).IsRequired();
          builder.Property(e => e.IsolationStrategy).HasDefaultValue("existing");
          builder.Property(e => e.CreatedAt).IsRequired();
      }
  }
  // Similar configurations for all entities
  ```
  **Acceptance**: DbContext configured, all entities mapped correctly

- [ ] 1.9 **Create Database Migrations**
  **What**: Define initial migration matching Go schema
  **Files**: `src/WeaveFleet.Infrastructure/Persistence/Migrations/`
  ```csharp
  // 20240101000000_InitialSchema.cs
  // Migration SQL matches Go migrations exactly
  ```
  **Acceptance**: `dotnet ef database update` creates identical schema to Go

- [ ] 1.10 **Implement Repository**
  **What**: Implement IFleetRepository using EF Core
  **Files**: `src/WeaveFleet.Infrastructure/Persistence/FleetRepository.cs`
  ```csharp
  internal sealed class FleetRepository : IFleetRepository
  {
      private readonly FleetDbContext _db;
      
      public FleetRepository(FleetDbContext db) => _db = db;
      
      // Implement all ~50 methods with proper async/await, mapping between
      // domain entities and EF entities, and CancellationToken propagation
  }
  ```
  **Acceptance**: All repository methods pass integration tests

- [ ] 1.11 **Create CLI Skeleton**
  **What**: Set up System.CommandLine-based CLI entry point
  **Files**: `src/WeaveFleet.Cli/`
  ```csharp
  // Program.cs
  var rootCommand = new RootCommand("Weave Agent Fleet");
  
  var serveCommand = new Command("serve", "Start the Weave server");
  serveCommand.AddOption(new Option<int>("--port", () => 3000, "Port to listen on"));
  serveCommand.AddOption(new Option<string>("--db", "Database file path"));
  serveCommand.AddOption(new Option<string>("--config", "Configuration file path"));
  serveCommand.SetHandler(async (context) => { /* ... */ });
  
  rootCommand.AddCommand(serveCommand);
  return await rootCommand.InvokeAsync(args);
  ```
  **Acceptance**: `weave serve --port 3000` starts the server

### Phase 1 Tests

- [ ] 1.T1 **Domain Entity Tests**
  **Files**: `tests/WeaveFleet.Domain.Tests/Entities/`
  - Test strongly-typed ID creation, parsing, equality
  - Test entity instantiation and validation

- [ ] 1.T2 **Repository Integration Tests**
  **Files**: `tests/WeaveFleet.Infrastructure.Tests/Persistence/`
  - Test all repository methods against in-memory SQLite
  - Test migration applies correctly

---

## Phase 2: Core Abstractions

### Objective
Implement the harness interface hierarchy, message bus, pool manager, workspace manager, and session manager.

### TODOs

- [ ] 2.1 **Define Harness Interfaces**
  **What**: Create the harness abstraction layer
  **Files**: `src/WeaveFleet.Application/Harnesses/`
  ```csharp
  // IHarness.cs
  namespace WeaveFleet.Application.Harnesses;
  
  public interface IHarness
  {
      string Name { get; }
      HarnessCapabilities Capabilities { get; }
      Task<Result<Unit>> CheckAvailabilityAsync(CancellationToken ct = default);
      Task<Result<IHarnessSession>> StartAsync(IEnvironment env, HarnessTask task, CancellationToken ct = default);
      Task<Result<IHarnessSession>> ResumeAsync(IEnvironment env, string sessionId, string prompt, CancellationToken ct = default);
  }
  
  // IHarnessSession.cs
  public interface IHarnessSession : IAsyncDisposable
  {
      string Id { get; }
      ChannelReader<HarnessEvent> Events { get; }
      HarnessSessionStatus Status { get; }
      bool IsPending { get; }
      Task<Result<Unit>> InitializeAsync(string prompt, CancellationToken ct = default);
      Task<Result<Unit>> SendPromptAsync(PromptOptions options, CancellationToken ct = default);
      Task<Result<Unit>> SendCommandAsync(CommandOptions options, CancellationToken ct = default);
      Task<Result<Unit>> AbortAsync(CancellationToken ct = default);
      Task<HarnessResult> WaitAsync(CancellationToken ct = default);
  }
  
  // IServerHarness.cs (for OpenCode-style server mode)
  public interface IServerHarness : IHarness
  {
      Task<Result<string>> SpawnServerAsync(IEnvironment env, CancellationToken ct = default);
      IHarnessClient CreateClient(string baseUrl);
      HealthCheckConfig? HealthCheckConfig { get; }
  }
  
  // IHarnessClient.cs
  public interface IHarnessClient
  {
      Task<Result<string>> CreateSessionAsync(CancellationToken ct = default);
      Task<Result<IHarnessSession>> ConnectSessionAsync(string sessionId, CancellationToken ct = default);
      Task<Result<Unit>> SendPromptAsync(string sessionId, PromptOptions options, CancellationToken ct = default);
      Task<Result<Unit>> SendCommandAsync(string sessionId, CommandOptions options, CancellationToken ct = default);
      Task<Result<Unit>> HealthCheckAsync(CancellationToken ct = default);
      Task<Result<IReadOnlyList<HarnessCommand>>> ListCommandsAsync(CancellationToken ct = default);
      Task<Result<IReadOnlyList<HarnessAgent>>> ListAgentsAsync(CancellationToken ct = default);
      Task<Result<IReadOnlyList<HarnessProvider>>> ListProvidersAsync(CancellationToken ct = default);
      Task<Result<IReadOnlyList<string>>> FindFilesAsync(string query, CancellationToken ct = default);
  }
  
  // HarnessCapabilities.cs
  public sealed record HarnessCapabilities(
      HarnessMode Mode,
      bool SupportsResume,
      bool SupportsAbort,
      bool SupportsStreaming,
      bool SupportsCommands,
      bool SupportsAgents,
      bool SupportsProviders,
      bool SupportsFileSearch,
      bool RequiresPromptAtStart
  );
  
  // HarnessEvent.cs
  public sealed record HarnessEvent(
      HarnessEventType Type,
      string SessionId,
      IReadOnlyDictionary<string, object?> Properties,
      DateTimeOffset Timestamp,
      byte[]? Raw = null
  );
  
  // HarnessEventType.cs
  public enum HarnessEventType
  {
      SessionStatus,
      SessionUpdated,
      SessionIdle,
      SessionDiff,
      MessageCreated,
      MessageUpdated,
      MessagePartCreated,
      MessagePartUpdated,
      MessagePartDelta,
      StepFinish,
      TitleUpdate,
      ServerConnected,
      ServerHeartbeat
  }
  ```
  **Acceptance**: Interfaces compile, match Go semantics

- [ ] 2.2 **Define Runtime Abstractions**
  **What**: Create process/environment abstractions
  **Files**: `src/WeaveFleet.Application/Runtime/`
  ```csharp
  // IRuntime.cs
  public interface IRuntime
  {
      Task<Result<IEnvironment>> ProvisionAsync(ProvisionOptions options, CancellationToken ct = default);
      Task<Result<Unit>> DestroyAsync(string envId, CancellationToken ct = default);
  }
  
  // IEnvironment.cs
  public interface IEnvironment : IAsyncDisposable
  {
      string Id { get; }
      string WorkDir { get; }
      string Address { get; } // "host:port"
      Task<Result<IProcess>> ExecAsync(string command, IReadOnlyList<string> args, ExecOptions options, CancellationToken ct = default);
  }
  
  // IProcess.cs
  public interface IProcess : IAsyncDisposable
  {
      int? Pid { get; }
      Stream? Stdout { get; }
      Stream? Stderr { get; }
      Task<int> WaitAsync(CancellationToken ct = default);
      Task KillAsync();
  }
  ```
  **Acceptance**: Abstractions defined for host runtime implementation

- [ ] 2.3 **Implement Result Pattern**
  **What**: Create Result<T> for error handling without exceptions
  **Files**: `src/WeaveFleet.Domain/Common/`
  ```csharp
  // Result.cs
  namespace WeaveFleet.Domain.Common;
  
  public readonly struct Result<T>
  {
      private readonly T? _value;
      private readonly Error? _error;
      
      public bool IsSuccess => _error is null;
      public bool IsFailure => !IsSuccess;
      public T Value => IsSuccess ? _value! : throw new InvalidOperationException("Result is failure");
      public Error Error => IsFailure ? _error! : throw new InvalidOperationException("Result is success");
      
      private Result(T value) { _value = value; _error = null; }
      private Result(Error error) { _value = default; _error = error; }
      
      public static Result<T> Success(T value) => new(value);
      public static Result<T> Failure(Error error) => new(error);
      
      public TResult Match<TResult>(Func<T, TResult> onSuccess, Func<Error, TResult> onFailure)
          => IsSuccess ? onSuccess(_value!) : onFailure(_error!);
  }
  
  public readonly record struct Unit
  {
      public static Unit Value => default;
  }
  
  public sealed record Error(string Code, string Message, Exception? Exception = null);
  ```
  **Acceptance**: Result pattern used throughout Application layer

- [ ] 2.4 **Implement Message Bus**
  **What**: Create System.Threading.Channels-based pub/sub
  **Files**: `src/WeaveFleet.Infrastructure/Messaging/`
  ```csharp
  // IMessageBus.cs (in Application)
  namespace WeaveFleet.Application.Messaging;
  
  public interface IMessageBus
  {
      IMessageSubscription Subscribe(params string[] topics);
      void Publish(string topic, string eventType, object data);
  }
  
  public interface IMessageSubscription : IDisposable
  {
      ChannelReader<BusEvent> Events { get; }
  }
  
  public sealed record BusEvent(string Topic, string Type, object Data, DateTimeOffset Timestamp);
  
  // MessageBus.cs (in Infrastructure)
  namespace WeaveFleet.Infrastructure.Messaging;
  
  internal sealed class MessageBus : IMessageBus
  {
      private readonly ConcurrentDictionary<ulong, Subscription> _subscriptions = new();
      private readonly ILogger<MessageBus> _logger;
      private ulong _nextId;
      
      public MessageBus(ILogger<MessageBus> logger) => _logger = logger;
      
      public IMessageSubscription Subscribe(params string[] topics)
      {
          var channel = Channel.CreateBounded<BusEvent>(new BoundedChannelOptions(256)
          {
              FullMode = BoundedChannelFullMode.DropOldest
          });
          
          var id = Interlocked.Increment(ref _nextId);
          var sub = new Subscription(id, topics, channel, this);
          _subscriptions.TryAdd(id, sub);
          return sub;
      }
      
      public void Publish(string topic, string eventType, object data)
      {
          var evt = new BusEvent(topic, eventType, data, DateTimeOffset.UtcNow);
          
          foreach (var sub in _subscriptions.Values)
          {
              if (!sub.Matches(topic)) continue;
              
              if (!sub.Channel.Writer.TryWrite(evt))
              {
                  _logger.LogWarning("Dropping event for slow subscriber {SubId} on topic {Topic}", sub.Id, topic);
              }
          }
      }
      
      private void Unsubscribe(ulong id)
      {
          if (_subscriptions.TryRemove(id, out var sub))
          {
              sub.Channel.Writer.Complete();
          }
      }
      
      private sealed class Subscription : IMessageSubscription
      {
          public ulong Id { get; }
          private readonly string[] _topics;
          public Channel<BusEvent> Channel { get; }
          private readonly MessageBus _bus;
          
          public ChannelReader<BusEvent> Events => Channel.Reader;
          
          public Subscription(ulong id, string[] topics, Channel<BusEvent> channel, MessageBus bus)
          {
              Id = id;
              _topics = topics;
              Channel = channel;
              _bus = bus;
          }
          
          public bool Matches(string topic)
              => _topics.Any(t => t == "*" || t == topic);
          
          public void Dispose() => _bus.Unsubscribe(Id);
      }
  }
  ```
  **Acceptance**: Bus supports topic subscriptions, non-blocking publish, wildcard "*"

- [ ] 2.5 **Implement Pool Manager**
  **What**: Create instance pool with LRU eviction and spawn deduplication
  **Files**: `src/WeaveFleet.Application/Pool/`
  ```csharp
  // IPoolManager.cs
  public interface IPoolManager
  {
      Task<Result<PoolInstance>> GetOrSpawnAsync(string directory, string harnessType, CancellationToken ct = default);
      PoolInstance? Get(InstanceId id);
      IReadOnlyList<PoolInstance> List();
      Task StopAsync(CancellationToken ct = default);
      Task StopInstanceAsync(InstanceId id, CancellationToken ct = default);
  }
  
  // PoolInstance.cs
  public sealed class PoolInstance
  {
      public InstanceId Id { get; }
      public string Directory { get; }
      public int Port { get; }
      public string? Url { get; }
      public IEnvironment Environment { get; }
      public string HarnessType { get; }
      public IHarnessClient? Client { get; }
      public IHarness Harness { get; }
      public DateTimeOffset CreatedAt { get; }
      
      private readonly Lock _lock = new();
      private PoolInstanceStatus _status;
      private DateTimeOffset _lastUsed;
      
      public PoolInstanceStatus Status { get { lock (_lock) return _status; } }
      public DateTimeOffset LastUsed { get { lock (_lock) return _lastUsed; } }
      
      public void Touch() { lock (_lock) _lastUsed = DateTimeOffset.UtcNow; }
      public void SetStatus(PoolInstanceStatus status) { lock (_lock) _status = status; }
      
      // Session operations delegate to Client or Harness
      public Task<Result<IHarnessSession>> StartSessionAsync(HarnessTask task, CancellationToken ct);
      public Task<Result<IHarnessSession>> ResumeSessionAsync(string sessionId, PromptOptions opts, CancellationToken ct);
  }
  
  // PoolManager.cs (in Infrastructure)
  internal sealed class PoolManager : IPoolManager, IHostedService
  {
      private readonly IOptions<FleetOptions> _options;
      private readonly IRuntime _runtime;
      private readonly IFleetRepository _repository;
      private readonly IMessageBus _bus;
      private readonly IHarnessRegistry _registry;
      private readonly ILogger<PoolManager> _logger;
      
      private readonly ConcurrentDictionary<InstanceId, PoolInstance> _instances = new();
      private readonly ConcurrentDictionary<string, PoolInstance> _byDir = new();
      private readonly SpawnGroup _spawnGroup = new();
      private readonly CancellationTokenSource _cts = new();
      
      // Implement GetOrSpawn with singleflight-style deduplication
      // Implement eviction loop as background task
      // Implement health monitoring for server-mode instances
  }
  
  // SpawnGroup.cs - singleflight pattern
  internal sealed class SpawnGroup
  {
      private readonly ConcurrentDictionary<string, TaskCompletionSource<Result<PoolInstance>>> _inflight = new();
      
      public async Task<Result<PoolInstance>> DoAsync(string key, Func<Task<Result<PoolInstance>>> factory)
      {
          var tcs = new TaskCompletionSource<Result<PoolInstance>>(TaskCreationOptions.RunContinuationsAsynchronously);
          
          if (_inflight.TryAdd(key, tcs))
          {
              try
              {
                  var result = await factory();
                  tcs.SetResult(result);
                  return result;
              }
              catch (Exception ex)
              {
                  tcs.SetException(ex);
                  throw;
              }
              finally
              {
                  _inflight.TryRemove(key, out _);
              }
          }
          
          // Another call is already in flight - wait for it
          if (_inflight.TryGetValue(key, out var existing))
          {
              return await existing.Task;
          }
          
          // Race condition - retry
          return await DoAsync(key, factory);
      }
  }
  ```
  **Acceptance**: Pool manages instances, deduplicates spawns, evicts idle/unhealthy

- [ ] 2.6 **Implement Harness Registry**
  **What**: Create harness registration and lookup
  **Files**: `src/WeaveFleet.Application/Harnesses/HarnessRegistry.cs`
  ```csharp
  public interface IHarnessRegistry
  {
      void Register(string name, IHarness harness);
      IHarness? Get(string name);
      IHarness MustGet(string name);
      IReadOnlyList<string> List();
      IReadOnlyList<string> ListAvailable(CancellationToken ct = default);
  }
  
  internal sealed class HarnessRegistry : IHarnessRegistry
  {
      private readonly ConcurrentDictionary<string, IHarness> _harnesses = new();
      
      // Implementation
  }
  ```
  **Acceptance**: Registry stores and retrieves harnesses, checks availability

- [ ] 2.7 **Implement Workspace Manager**
  **What**: Create workspace creation with isolation strategies
  **Files**: `src/WeaveFleet.Application/Workspaces/`
  ```csharp
  // IWorkspaceManager.cs
  public interface IWorkspaceManager
  {
      Task<Result<Workspace>> CreateAsync(WorkspaceCreateOptions options, CancellationToken ct = default);
      Task<Result<Unit>> CleanupAsync(WorkspaceId id, CancellationToken ct = default);
  }
  
  public sealed record WorkspaceCreateOptions(
      string SourceDirectory,
      IsolationStrategy Strategy,
      string? Branch = null
  );
  
  // WorkspaceManager.cs
  internal sealed class WorkspaceManager : IWorkspaceManager
  {
      private readonly IFleetRepository _repository;
      private readonly ILogger<WorkspaceManager> _logger;
      
      // Implement existing, worktree, clone strategies
      // Git operations via process execution
  }
  ```
  **Acceptance**: All three isolation strategies work correctly

- [ ] 2.8 **Implement Session Manager**
  **What**: Create session lifecycle management
  **Files**: `src/WeaveFleet.Application/Sessions/`
  ```csharp
  // ISessionManager.cs
  public interface ISessionManager
  {
      Task<Result<Session>> CreateAsync(SessionCreateOptions options, CancellationToken ct = default);
      Task<Result<Session>> GetAsync(SessionId id, CancellationToken ct = default);
      Task<Result<IReadOnlyList<Session>>> ListAsync(ListSessionsOptions options, CancellationToken ct = default);
      Task<Result<bool>> DeleteAsync(SessionId id, CancellationToken ct = default);
      Task<Result<Unit>> SendPromptAsync(SessionId id, SessionPromptOptions options, CancellationToken ct = default);
      Task<Result<Unit>> SendCommandAsync(SessionId id, SessionCommandOptions options, CancellationToken ct = default);
      Task<Result<Unit>> AbortAsync(SessionId id, CancellationToken ct = default);
  }
  
  // SessionManager.cs
  internal sealed class SessionManager : ISessionManager
  {
      private readonly IFleetRepository _repository;
      private readonly IPoolManager _pool;
      private readonly IWorkspaceManager _workspace;
      private readonly IMessageBus _bus;
      private readonly ILogger<SessionManager> _logger;
      
      private readonly ConcurrentDictionary<SessionId, CancellationTokenSource> _forwarders = new();
      private readonly ConcurrentDictionary<SessionId, IHarnessSession> _pendingSessions = new();
      private readonly ConcurrentDictionary<MessageId, List<OrphanedPartEntry>> _orphanedParts = new();
      
      // Implement Create, SendPrompt, event forwarding, message persistence
      // Handle pending sessions (RequiresPromptAtStart)
      // Rewrite session IDs in events
  }
  ```
  **Acceptance**: Full session lifecycle works, events flow to bus

- [ ] 2.9 **Implement Host Runtime**
  **What**: Create local process-based runtime
  **Files**: `src/WeaveFleet.Infrastructure/Runtime/`
  ```csharp
  // HostRuntime.cs
  internal sealed class HostRuntime : IRuntime
  {
      private readonly ConcurrentDictionary<string, HostEnvironment> _environments = new();
      private readonly PortAllocator _portAllocator;
      private readonly ILogger<HostRuntime> _logger;
      
      // Implement Provision, Destroy
      // Manage process lifecycles
  }
  
  // PortAllocator.cs
  internal sealed class PortAllocator
  {
      private readonly int _start;
      private readonly int _end;
      private readonly HashSet<int> _allocated = [];
      private readonly Lock _lock = new();
      
      public int Allocate()
      {
          lock (_lock)
          {
              for (var port = _start; port <= _end; port++)
              {
                  if (_allocated.Add(port) && IsPortAvailable(port))
                      return port;
              }
              throw new InvalidOperationException("No ports available");
          }
      }
      
      public void Release(int port)
      {
          lock (_lock) _allocated.Remove(port);
      }
      
      private static bool IsPortAvailable(int port)
      {
          try
          {
              using var listener = new TcpListener(IPAddress.Loopback, port);
              listener.Start();
              listener.Stop();
              return true;
          }
          catch { return false; }
      }
  }
  ```
  **Acceptance**: Runtime provisions environments, executes processes

### Phase 2 Tests

- [ ] 2.T1 **Message Bus Tests**
  **Files**: `tests/WeaveFleet.Infrastructure.Tests/Messaging/`
  - Test topic subscription and matching
  - Test non-blocking publish with full buffer
  - Test wildcard subscription

- [ ] 2.T2 **Pool Manager Tests**
  **Files**: `tests/WeaveFleet.Application.Tests/Pool/`
  - Test spawn deduplication (concurrent calls coalesce)
  - Test LRU eviction
  - Test idle timeout eviction

- [ ] 2.T3 **Session Manager Tests**
  **Files**: `tests/WeaveFleet.Application.Tests/Sessions/`
  - Test session creation and event forwarding
  - Test pending session flow (Initialize)
  - Test message persistence

---

## Phase 3: API & Real-time

### Objective
Implement the HTTP API and SignalR hub for real-time communication.

### TODOs

- [ ] 3.1 **Create API Project Structure**
  **What**: Set up Minimal APIs with typed endpoints
  **Files**: `src/WeaveFleet.Api/`
  ```csharp
  // Program.cs
  var builder = WebApplication.CreateBuilder(args);
  
  builder.Services.AddSignalR();
  builder.Services.AddHealthChecks();
  builder.Services.AddEndpointsApiExplorer();
  
  // Add application services
  builder.Services.AddFleetServices(builder.Configuration);
  
  var app = builder.Build();
  
  // Map endpoints
  app.MapHealthChecks("/healthz");
  app.MapHealthChecks("/readyz");
  app.MapHub<FleetHub>("/ws");
  app.MapFleetEndpoints();
  
  // Static SPA serving
  app.UseStaticFiles();
  app.MapFallbackToFile("index.html");
  
  await app.RunAsync();
  ```
  **Acceptance**: API starts, health checks respond

- [ ] 3.2 **Implement Session Endpoints**
  **What**: Create all session-related API endpoints
  **Files**: `src/WeaveFleet.Api/Endpoints/SessionEndpoints.cs`
  ```csharp
  internal static class SessionEndpoints
  {
      public static void MapSessionEndpoints(this IEndpointRouteBuilder app)
      {
          var group = app.MapGroup("/api/sessions");
          
          group.MapGet("/", ListSessions);
          group.MapPost("/", CreateSession);
          group.MapGet("/{id}", GetSession);
          group.MapPatch("/{id}", RenameSession);
          group.MapDelete("/{id}", DeleteSession);
          group.MapPost("/{id}/prompt", SendPrompt);
          group.MapPost("/{id}/command", SendCommand);
          group.MapPost("/{id}/abort", AbortSession);
          group.MapGet("/{id}/messages", GetMessages);
          group.MapGet("/{id}/status", GetSessionStatus);
          group.MapGet("/{id}/diffs", GetDiffs);
      }
      
      private static async Task<IResult> ListSessions(
          ISessionManager sessions,
          IFleetRepository repo,
          IPoolManager pool,
          [AsParameters] ListSessionsRequest request,
          CancellationToken ct)
      {
          // Implementation
      }
      
      // All endpoint handlers...
  }
  ```
  **Acceptance**: All session endpoints match Go behavior

- [ ] 3.3 **Implement Instance Endpoints**
  **What**: Create instance-related API endpoints
  **Files**: `src/WeaveFleet.Api/Endpoints/InstanceEndpoints.cs`
  ```csharp
  internal static class InstanceEndpoints
  {
      public static void MapInstanceEndpoints(this IEndpointRouteBuilder app)
      {
          var group = app.MapGroup("/api/instances");
          
          group.MapGet("/", ListInstances);
          group.MapGet("/{id}", GetInstance);
          group.MapDelete("/{id}", StopInstance);
          group.MapGet("/{id}/commands", GetCommands);
          group.MapGet("/{id}/agents", GetAgents);
          group.MapGet("/{id}/models", GetModels);
          group.MapGet("/{id}/find/files", FindFiles);
      }
  }
  ```
  **Acceptance**: All instance endpoints work

- [ ] 3.4 **Implement Fleet Endpoints**
  **What**: Create fleet management endpoints
  **Files**: `src/WeaveFleet.Api/Endpoints/FleetEndpoints.cs`
  ```csharp
  internal static class FleetEndpoints
  {
      public static void MapFleetEndpoints(this IEndpointRouteBuilder app)
      {
          var group = app.MapGroup("/api/fleet");
          
          group.MapGet("/status", GetStatus);
          group.MapGet("/summary", GetSummary);
          group.MapPost("/spawn", Spawn);
          group.MapPost("/stop-all", StopAll);
      }
  }
  ```
  **Acceptance**: Fleet endpoints return correct aggregated data

- [ ] 3.5 **Implement Auxiliary Endpoints**
  **What**: Create remaining endpoints (directories, harnesses, config, etc.)
  **Files**: `src/WeaveFleet.Api/Endpoints/`
  ```csharp
  // DirectoryEndpoints.cs
  // HarnessEndpoints.cs
  // WorkspaceRootEndpoints.cs
  // ConfigEndpoints.cs
  ```
  **Acceptance**: All ~40 endpoints implemented

- [ ] 3.6 **Implement SignalR Hub**
  **What**: Create SignalR hub replacing manual WebSocket implementation
  **Files**: `src/WeaveFleet.Api/Hubs/FleetHub.cs`
  ```csharp
  namespace WeaveFleet.Api.Hubs;
  
  public sealed class FleetHub : Hub
  {
      private readonly IMessageBus _bus;
      private readonly ILogger<FleetHub> _logger;
      
      public FleetHub(IMessageBus bus, ILogger<FleetHub> logger)
      {
          _bus = bus;
          _logger = logger;
      }
      
      public async Task Subscribe(string[] topics)
      {
          foreach (var topic in topics)
          {
              await Groups.AddToGroupAsync(Context.ConnectionId, topic);
          }
          
          await Clients.Caller.SendAsync("subscribed", new { topics });
      }
      
      public async Task Unsubscribe(string[] topics)
      {
          foreach (var topic in topics)
          {
              await Groups.RemoveFromGroupAsync(Context.ConnectionId, topic);
          }
      }
      
      public override async Task OnConnectedAsync()
      {
          _logger.LogDebug("Client connected: {ConnectionId}", Context.ConnectionId);
          await base.OnConnectedAsync();
      }
      
      public override async Task OnDisconnectedAsync(Exception? exception)
      {
          _logger.LogDebug("Client disconnected: {ConnectionId}", Context.ConnectionId);
          await base.OnDisconnectedAsync(exception);
      }
  }
  ```
  **Acceptance**: Clients can subscribe to topics, receive events

- [ ] 3.7 **Implement Bus-to-SignalR Bridge**
  **What**: Create background service that forwards bus events to SignalR
  **Files**: `src/WeaveFleet.Api/Services/SignalRBridgeService.cs`
  ```csharp
  internal sealed class SignalRBridgeService : BackgroundService
  {
      private readonly IMessageBus _bus;
      private readonly IHubContext<FleetHub> _hubContext;
      private readonly ILogger<SignalRBridgeService> _logger;
      
      protected override async Task ExecuteAsync(CancellationToken stoppingToken)
      {
          using var subscription = _bus.Subscribe("*");
          
          await foreach (var evt in subscription.Events.ReadAllAsync(stoppingToken))
          {
              await _hubContext.Clients.Group(evt.Topic).SendAsync("event", new
              {
                  type = evt.Type,
                  topic = evt.Topic,
                  data = evt.Data,
                  timestamp = evt.Timestamp
              }, stoppingToken);
          }
      }
  }
  ```
  **Acceptance**: Events published to bus reach SignalR clients

- [ ] 3.8 **Add Middleware Pipeline**
  **What**: Configure cross-cutting concerns
  **Files**: `src/WeaveFleet.Api/`
  ```csharp
  // Middleware/RequestLoggingMiddleware.cs
  // Middleware/ExceptionHandlingMiddleware.cs
  // Extensions/MiddlewareExtensions.cs
  
  app.UseMiddleware<RequestLoggingMiddleware>();
  app.UseMiddleware<ExceptionHandlingMiddleware>();
  app.UseResponseCompression();
  app.UseCors();
  ```
  **Acceptance**: Requests logged, errors handled gracefully

- [ ] 3.9 **Implement Request/Response DTOs**
  **What**: Create strongly-typed DTOs for API contracts
  **Files**: `src/WeaveFleet.Api/Contracts/`
  ```csharp
  // Sessions/
  public sealed record CreateSessionRequest(
      string Directory,
      string? Title,
      string? Prompt,
      IsolationStrategy IsolationStrategy,
      string? Branch,
      string? ParentSessionId,
      OnCompletePayload? OnComplete,
      string? HarnessType
  );
  
  public sealed record SessionListItemResponse(
      string InstanceId,
      string WorkspaceId,
      string WorkspaceDirectory,
      string? WorkspaceDisplayName,
      string IsolationStrategy,
      string? SourceDirectory,
      string? Branch,
      string SessionStatus,
      SessionStub Session,
      string InstanceStatus,
      string DbId,
      string? ParentSessionId,
      string? ActivityStatus,
      string LifecycleStatus,
      long? TotalTokens,
      decimal? TotalCost
  );
  
  // ... all DTOs matching Go API contracts
  ```
  **Acceptance**: API contract matches Go exactly

### Phase 3 Tests

- [ ] 3.T1 **Endpoint Integration Tests**
  **Files**: `tests/WeaveFleet.Api.Tests/Endpoints/`
  - Test all endpoints with WebApplicationFactory
  - Verify response shapes match expectations

- [ ] 3.T2 **SignalR Hub Tests**
  **Files**: `tests/WeaveFleet.Api.Tests/Hubs/`
  - Test topic subscription
  - Test event delivery to subscribed clients

---

## Phase 4: Harness Implementations

### Objective
Implement OpenCode and Claude Code harnesses matching Go behavior.

### TODOs

- [ ] 4.1 **Implement OpenCode Harness**
  **What**: Create HTTP+SSE server-mode harness
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/`
  ```csharp
  // OpenCodeHarness.cs
  internal sealed class OpenCodeHarness : IServerHarness
  {
      private readonly IOptions<OpenCodeOptions> _options;
      private readonly ILogger<OpenCodeHarness> _logger;
      
      public string Name => "opencode";
      
      public HarnessCapabilities Capabilities => new(
          Mode: HarnessMode.Server,
          SupportsResume: true,
          SupportsAbort: false,
          SupportsStreaming: true,
          SupportsCommands: true,
          SupportsAgents: true,
          SupportsProviders: true,
          SupportsFileSearch: true,
          RequiresPromptAtStart: false
      );
      
      public async Task<Result<Unit>> CheckAvailabilityAsync(CancellationToken ct)
      {
          // Check if opencode binary exists on PATH
      }
      
      public async Task<Result<string>> SpawnServerAsync(IEnvironment env, CancellationToken ct)
      {
          // Spawn `opencode serve --hostname --port`
          // Wait for readiness message on stdout
          // Return base URL
      }
      
      public IHarnessClient CreateClient(string baseUrl)
          => new OpenCodeClient(baseUrl, _logger);
  }
  
  // OpenCodeClient.cs
  internal sealed class OpenCodeClient : IHarnessClient
  {
      private readonly string _baseUrl;
      private readonly HttpClient _http;
      private readonly ILogger _logger;
      
      // Implement all client methods
      // CreateSession, SendPrompt, SSE streaming, etc.
  }
  
  // OpenCodeSession.cs
  internal sealed class OpenCodeSession : IHarnessSession
  {
      private readonly string _id;
      private readonly OpenCodeClient _client;
      private readonly Channel<HarnessEvent> _events;
      private readonly CancellationTokenSource _streamCts;
      
      // Implement session with SSE event stream
  }
  
  // SseStreamReader.cs
  internal sealed class SseStreamReader
  {
      // Parse SSE format: event: type\ndata: json\n\n
  }
  ```
  **Acceptance**: OpenCode harness spawns servers, streams events

- [ ] 4.2 **Implement Claude Code Harness**
  **What**: Create subprocess NDJSON harness
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/ClaudeCode/`
  ```csharp
  // ClaudeCodeHarness.cs
  internal sealed class ClaudeCodeHarness : IHarness
  {
      public string Name => "claude";
      
      public HarnessCapabilities Capabilities => new(
          Mode: HarnessMode.Subprocess,
          SupportsResume: true,
          SupportsAbort: true,
          SupportsStreaming: true,
          SupportsCommands: false,
          SupportsAgents: false,
          SupportsProviders: false,
          SupportsFileSearch: false,
          RequiresPromptAtStart: true
      );
      
      public async Task<Result<IHarnessSession>> StartAsync(IEnvironment env, HarnessTask task, CancellationToken ct)
      {
          if (string.IsNullOrEmpty(task.Prompt))
          {
              // Return pending session
              return Result.Success<IHarnessSession>(new ClaudeCodePendingSession(this, env, _logger));
          }
          
          // Spawn subprocess with prompt
          var args = new[] { "-p", "--output-format", "stream-json", "--verbose", 
                             "--dangerously-skip-permissions", "--", task.Prompt };
          
          var proc = await env.ExecAsync("claude", args, new ExecOptions { PipeStdout = true, PipeStderr = true }, ct);
          return Result.Success<IHarnessSession>(new ClaudeCodeSession(proc, _logger));
      }
  }
  
  // ClaudeCodeSession.cs
  internal sealed class ClaudeCodeSession : IHarnessSession
  {
      private readonly IProcess _process;
      private readonly Channel<HarnessEvent> _events;
      private readonly Task _readerTask;
      
      // Read NDJSON from stdout, transform to HarnessEvent
  }
  
  // ClaudeCodePendingSession.cs
  internal sealed class ClaudeCodePendingSession : IHarnessSession
  {
      // Deferred session - Initialize() spawns the real subprocess
  }
  
  // NdjsonReader.cs
  internal sealed class NdjsonReader
  {
      // Parse NDJSON lines from stream
  }
  
  // EventTransformer.cs
  internal sealed class EventTransformer
  {
      // Transform Claude Code events to canonical format
  }
  ```
  **Acceptance**: Claude Code harness works with subprocess mode

- [ ] 4.3 **Register Harnesses at Startup**
  **What**: Wire harnesses into DI
  **Files**: `src/WeaveFleet.Infrastructure/DependencyInjection.cs`
  ```csharp
  public static IServiceCollection AddFleetInfrastructure(this IServiceCollection services, IConfiguration config)
  {
      // Register harnesses
      services.AddSingleton<IHarness, OpenCodeHarness>();
      services.AddSingleton<IHarness, ClaudeCodeHarness>();
      
      // Register harness registry
      services.AddSingleton<IHarnessRegistry>(sp =>
      {
          var registry = new HarnessRegistry();
          foreach (var harness in sp.GetServices<IHarness>())
          {
              registry.Register(harness.Name, harness);
          }
          return registry;
      });
      
      return services;
  }
  ```
  **Acceptance**: Harnesses registered and available

### Phase 4 Tests

- [ ] 4.T1 **OpenCode Harness Tests**
  **Files**: `tests/WeaveFleet.Infrastructure.Tests/Harnesses/OpenCode/`
  - Test SSE parsing
  - Test event transformation
  - Integration test with real opencode binary (optional)

- [ ] 4.T2 **Claude Code Harness Tests**
  **Files**: `tests/WeaveFleet.Infrastructure.Tests/Harnesses/ClaudeCode/`
  - Test NDJSON parsing
  - Test pending session flow
  - Test event transformation

---

## Phase 5: Observability & Resilience

### Objective
Add OpenTelemetry, health checks, circuit breakers, and event replay.

### TODOs

- [ ] 5.1 **Add OpenTelemetry Integration**
  **What**: Configure traces, metrics, and structured logging
  **Files**: `src/WeaveFleet.Api/Extensions/ObservabilityExtensions.cs`
  ```csharp
  public static IServiceCollection AddFleetObservability(this IServiceCollection services, IConfiguration config)
  {
      services.AddOpenTelemetry()
          .ConfigureResource(r => r.AddService("weave-fleet"))
          .WithTracing(tracing => tracing
              .AddAspNetCoreInstrumentation()
              .AddHttpClientInstrumentation()
              .AddSource("WeaveFleet.*")
              .AddOtlpExporter())
          .WithMetrics(metrics => metrics
              .AddAspNetCoreInstrumentation()
              .AddHttpClientInstrumentation()
              .AddMeter("WeaveFleet.*")
              .AddOtlpExporter());
      
      services.AddLogging(logging =>
      {
          logging.AddOpenTelemetry(options =>
          {
              options.IncludeScopes = true;
              options.AddOtlpExporter();
          });
      });
      
      return services;
  }
  ```
  **Acceptance**: Traces/metrics export to OTLP endpoint

- [ ] 5.2 **Add Custom Instrumentation**
  **What**: Instrument critical operations with spans and metrics
  **Files**: `src/WeaveFleet.Infrastructure/Telemetry/`
  ```csharp
  // FleetMetrics.cs
  internal static class FleetMetrics
  {
      private static readonly Meter Meter = new("WeaveFleet.Fleet");
      
      public static readonly Counter<long> SessionsCreated = Meter.CreateCounter<long>("fleet.sessions.created");
      public static readonly Counter<long> TokensUsed = Meter.CreateCounter<long>("fleet.tokens.used");
      public static readonly Histogram<double> SessionDuration = Meter.CreateHistogram<double>("fleet.session.duration");
      public static readonly UpDownCounter<int> ActiveInstances = Meter.CreateUpDownCounter<int>("fleet.instances.active");
  }
  
  // FleetActivitySource.cs
  internal static class FleetActivitySource
  {
      public static readonly ActivitySource Source = new("WeaveFleet.Fleet");
      
      public static Activity? StartSessionCreate() => Source.StartActivity("session.create");
      public static Activity? StartHarnessSpawn() => Source.StartActivity("harness.spawn");
  }
  ```
  **Acceptance**: Custom metrics/spans visible in telemetry

- [ ] 5.3 **Enhance Health Checks**
  **What**: Add detailed health check endpoints
  **Files**: `src/WeaveFleet.Api/HealthChecks/`
  ```csharp
  // DatabaseHealthCheck.cs
  internal sealed class DatabaseHealthCheck : IHealthCheck
  {
      private readonly FleetDbContext _db;
      
      public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct)
      {
          try
          {
              await _db.Database.CanConnectAsync(ct);
              return HealthCheckResult.Healthy();
          }
          catch (Exception ex)
          {
              return HealthCheckResult.Unhealthy("Database unavailable", ex);
          }
      }
  }
  
  // HarnessHealthCheck.cs
  internal sealed class HarnessHealthCheck : IHealthCheck
  {
      private readonly IHarnessRegistry _registry;
      
      // Check at least one harness is available
  }
  ```
  **Acceptance**: `/healthz` and `/readyz` report component status

- [ ] 5.4 **Add Circuit Breaker for Harness Communication**
  **What**: Use Polly for resilience
  **Files**: `src/WeaveFleet.Infrastructure/Resilience/`
  ```csharp
  // Configure Polly policies for HTTP clients
  services.AddHttpClient<OpenCodeClient>()
      .AddResilienceHandler("harness", builder =>
      {
          builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions
          {
              FailureRatio = 0.5,
              SamplingDuration = TimeSpan.FromSeconds(10),
              MinimumThroughput = 5,
              BreakDuration = TimeSpan.FromSeconds(30)
          });
          
          builder.AddRetry(new RetryStrategyOptions
          {
              MaxRetryAttempts = 3,
              Delay = TimeSpan.FromMilliseconds(200),
              BackoffType = DelayBackoffType.Exponential
          });
      });
  ```
  **Acceptance**: Failed harness calls trigger circuit breaker

- [ ] 5.5 **Implement Event Replay with Sequence IDs**
  **What**: Add sequence numbers for WebSocket reconnect replay
  **Files**: `src/WeaveFleet.Infrastructure/Messaging/`
  ```csharp
  // EventSequencer.cs
  internal sealed class EventSequencer
  {
      private readonly ConcurrentDictionary<string, long> _sequences = new();
      private readonly ConcurrentDictionary<string, BoundedQueue<SequencedEvent>> _history = new();
      
      public SequencedEvent Sequence(string topic, BusEvent evt)
      {
          var seq = _sequences.AddOrUpdate(topic, 1, (_, v) => v + 1);
          var sequenced = new SequencedEvent(seq, evt);
          
          // Store in bounded history for replay
          var queue = _history.GetOrAdd(topic, _ => new BoundedQueue<SequencedEvent>(1000));
          queue.Enqueue(sequenced);
          
          return sequenced;
      }
      
      public IReadOnlyList<SequencedEvent> GetSince(string topic, long lastSeq)
      {
          if (!_history.TryGetValue(topic, out var queue))
              return [];
          
          return queue.Where(e => e.Sequence > lastSeq).ToList();
      }
  }
  
  // FleetHub with replay support
  public async Task Subscribe(string[] topics, long? lastEventId = null)
  {
      foreach (var topic in topics)
      {
          await Groups.AddToGroupAsync(Context.ConnectionId, topic);
          
          // Replay missed events
          if (lastEventId.HasValue)
          {
              var missed = _sequencer.GetSince(topic, lastEventId.Value);
              foreach (var evt in missed)
              {
                  await Clients.Caller.SendAsync("event", evt);
              }
          }
      }
  }
  ```
  **Acceptance**: Clients can reconnect and receive missed events

- [ ] 5.6 **Implement Pool Recovery on Startup**
  **What**: Recover orphan processes and clean up stale instances
  **Files**: `src/WeaveFleet.Infrastructure/Pool/`
  ```csharp
  // PoolRecoveryService.cs
  internal sealed class PoolRecoveryService : IHostedService
  {
      private readonly IFleetRepository _repository;
      private readonly ILogger<PoolRecoveryService> _logger;
      
      public async Task StartAsync(CancellationToken ct)
      {
          // Mark all instances as stopped (they're orphaned if still "running")
          var count = await _repository.MarkAllInstancesStoppedAsync(ct);
          _logger.LogInformation("Marked {Count} orphan instances as stopped", count);
          
          // Mark non-terminal sessions as disconnected
          var sessionCount = await _repository.MarkAllNonTerminalSessionsStoppedAsync(ct);
          _logger.LogInformation("Marked {Count} orphan sessions as disconnected", sessionCount);
      }
      
      public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
  }
  ```
  **Acceptance**: Stale data cleaned on startup

### Phase 5 Tests

- [ ] 5.T1 **Health Check Tests**
  - Test health endpoints return correct status
  - Test degraded state detection

- [ ] 5.T2 **Event Replay Tests**
  - Test sequence ID assignment
  - Test replay on reconnect

---

## Phase 6: Ambitious Features

### Objective
Go beyond Go parity with advanced capabilities.

### TODOs

- [ ] 6.1 **Plugin System for Harnesses**
  **What**: Allow loading harnesses from external assemblies
  **Files**: `src/WeaveFleet.Application/Plugins/`
  ```csharp
  // IHarnessPlugin.cs
  public interface IHarnessPlugin
  {
      string Name { get; }
      IHarness CreateHarness(IServiceProvider services);
  }
  
  // PluginLoader.cs
  internal sealed class PluginLoader
  {
      public IReadOnlyList<IHarnessPlugin> LoadPlugins(string pluginsDirectory)
      {
          var plugins = new List<IHarnessPlugin>();
          
          foreach (var dll in Directory.GetFiles(pluginsDirectory, "*.dll"))
          {
              var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
              foreach (var type in assembly.GetExportedTypes())
              {
                  if (typeof(IHarnessPlugin).IsAssignableFrom(type) && !type.IsAbstract)
                  {
                      var plugin = (IHarnessPlugin)Activator.CreateInstance(type)!;
                      plugins.Add(plugin);
                  }
              }
          }
          
          return plugins;
      }
  }
  ```
  **Acceptance**: External harness plugins can be loaded

- [ ] 6.2 **Workspace-First API**
  **What**: Decouple workspace management from sessions
  **Files**: `src/WeaveFleet.Api/Endpoints/WorkspaceEndpoints.cs`
  ```csharp
  // New API endpoints
  // POST /api/workspaces - Create workspace
  // GET /api/workspaces - List workspaces
  // GET /api/workspaces/{id} - Get workspace
  // DELETE /api/workspaces/{id} - Delete workspace
  // POST /api/workspaces/{id}/sessions - Create session in workspace
  ```
  **Acceptance**: Workspaces can be managed independently

- [ ] 6.3 **Unix Domain Socket Support**
  **What**: Allow binding to Unix sockets
  **Files**: `src/WeaveFleet.Api/`
  ```csharp
  // In Program.cs
  if (!string.IsNullOrEmpty(options.UnixSocket))
  {
      builder.WebHost.ConfigureKestrel(kestrel =>
      {
          kestrel.ListenUnixSocket(options.UnixSocket);
      });
  }
  ```
  **Acceptance**: Server can listen on Unix socket

- [ ] 6.4 **Multi-Profile Support**
  **What**: Support multiple configuration profiles
  **Files**: `src/WeaveFleet.Application/Configuration/`
  ```csharp
  // ProfileManager.cs
  internal sealed class ProfileManager
  {
      private readonly string _profilesDir;
      
      public IReadOnlyList<string> ListProfiles();
      public FleetOptions LoadProfile(string name);
      public void SaveProfile(string name, FleetOptions options);
      public void DeleteProfile(string name);
  }
  ```
  **Acceptance**: Multiple profiles can be saved and loaded

- [ ] 6.5 **Container Isolation Strategy (Design Only)**
  **What**: Design for Docker/Podman workspace isolation
  **Files**: `docs/container-isolation.md`
  ```markdown
  # Container Isolation Strategy
  
  ## Overview
  Add `IsolationStrategy.Container` that provisions workspaces inside containers.
  
  ## Implementation Plan
  1. ContainerRuntime : IRuntime
  2. Docker/Podman detection
  3. Image management
  4. Volume mounting for workspace files
  5. Network isolation
  ```
  **Acceptance**: Design documented for future implementation

- [ ] 6.6 **Remote Fleet Architecture (Design Only)**
  **What**: Design for distributed fleet coordination
  **Files**: `docs/remote-fleet.md`
  ```markdown
  # Remote Fleet Architecture
  
  ## Overview
  Support running fleet nodes on remote machines with central coordination.
  
  ## Components
  - Fleet Coordinator (central)
  - Fleet Agent (per-node)
  - SignalR backplane for cross-node communication
  - gRPC for agent control
  
  ## Implementation Plan
  (detailed design)
  ```
  **Acceptance**: Design documented for future implementation

### Phase 6 Tests

- [ ] 6.T1 **Plugin System Tests**
  - Test plugin loading
  - Test invalid plugin handling

- [ ] 6.T2 **Workspace API Tests**
  - Test workspace CRUD operations
  - Test session creation in workspace

---

## Verification

### Build Verification
- [ ] `dotnet build --configuration Release` completes with 0 warnings
- [ ] `dotnet publish -c Release` produces working binary
- [ ] Native AOT publish works (if enabled)

### Test Verification
- [ ] `dotnet test` passes all tests
- [ ] Code coverage > 80% for Application layer
- [ ] Integration tests pass with real SQLite

### Runtime Verification
- [ ] Server starts on `weave serve`
- [ ] Health endpoints respond correctly
- [ ] Frontend loads and connects via SignalR
- [ ] Can create session with OpenCode harness
- [ ] Can create session with Claude Code harness
- [ ] Events stream to frontend in real-time
- [ ] Messages persist and reload on refresh
- [ ] Pool evicts idle instances
- [ ] Pool health-checks server instances

### API Compatibility
- [ ] All ~40 endpoints implemented
- [ ] Response shapes match Go implementation
- [ ] WebSocket protocol compatible (subscribe/unsubscribe/event)

---

## File Summary

### New Files to Create

```
WeaveFleet.sln
Directory.Build.props
Directory.Packages.props
global.json
.editorconfig

src/
  WeaveFleet.Domain/
    WeaveFleet.Domain.csproj
    Common/Result.cs
    Common/Error.cs
    Identifiers/WorkspaceId.cs
    Identifiers/SessionId.cs
    Identifiers/InstanceId.cs
    Identifiers/MessageId.cs
    Identifiers/MessagePartId.cs
    Identifiers/SessionCallbackId.cs
    Identifiers/WorkspaceRootId.cs
    Enums/SessionStatus.cs
    Enums/IsolationStrategy.cs
    Enums/InstanceStatus.cs
    Enums/MessageRole.cs
    Enums/MessagePartType.cs
    Enums/CallbackStatus.cs
    Enums/HarnessMode.cs
    Enums/HarnessSessionStatus.cs
    Entities/Workspace.cs
    Entities/Instance.cs
    Entities/Session.cs
    Entities/Message.cs
    Entities/MessagePart.cs
    Entities/SessionCallback.cs
    Entities/WorkspaceRoot.cs
    
  WeaveFleet.Application/
    WeaveFleet.Application.csproj
    Configuration/FleetOptions.cs
    Configuration/HarnessOptions.cs
    Persistence/IFleetRepository.cs
    Persistence/ListSessionsOptions.cs
    Persistence/SessionStatusCounts.cs
    Persistence/FleetTokenTotals.cs
    Persistence/MessageWithParts.cs
    Messaging/IMessageBus.cs
    Messaging/IMessageSubscription.cs
    Messaging/BusEvent.cs
    Harnesses/IHarness.cs
    Harnesses/IHarnessSession.cs
    Harnesses/IServerHarness.cs
    Harnesses/IHarnessClient.cs
    Harnesses/IHarnessRegistry.cs
    Harnesses/HarnessCapabilities.cs
    Harnesses/HarnessEvent.cs
    Harnesses/HarnessEventType.cs
    Harnesses/HarnessTask.cs
    Harnesses/PromptOptions.cs
    Harnesses/CommandOptions.cs
    Harnesses/HarnessResult.cs
    Harnesses/HarnessRegistry.cs
    Runtime/IRuntime.cs
    Runtime/IEnvironment.cs
    Runtime/IProcess.cs
    Runtime/ProvisionOptions.cs
    Runtime/ExecOptions.cs
    Pool/IPoolManager.cs
    Pool/PoolInstance.cs
    Pool/PoolInstanceStatus.cs
    Workspaces/IWorkspaceManager.cs
    Workspaces/WorkspaceCreateOptions.cs
    Sessions/ISessionManager.cs
    Sessions/SessionCreateOptions.cs
    Sessions/SessionPromptOptions.cs
    Sessions/SessionCommandOptions.cs
    DependencyInjection.cs
    
  WeaveFleet.Infrastructure/
    WeaveFleet.Infrastructure.csproj
    Persistence/FleetDbContext.cs
    Persistence/FleetRepository.cs
    Persistence/Entities/WorkspaceEntity.cs
    Persistence/Entities/InstanceEntity.cs
    Persistence/Entities/SessionEntity.cs
    Persistence/Entities/MessageEntity.cs
    Persistence/Entities/MessagePartEntity.cs
    Persistence/Entities/SessionCallbackEntity.cs
    Persistence/Entities/WorkspaceRootEntity.cs
    Persistence/Configurations/WorkspaceConfiguration.cs
    Persistence/Configurations/InstanceConfiguration.cs
    Persistence/Configurations/SessionConfiguration.cs
    Persistence/Configurations/MessageConfiguration.cs
    Persistence/Configurations/MessagePartConfiguration.cs
    Persistence/Configurations/SessionCallbackConfiguration.cs
    Persistence/Configurations/WorkspaceRootConfiguration.cs
    Persistence/Migrations/20240101000000_InitialSchema.cs
    Messaging/MessageBus.cs
    Pool/PoolManager.cs
    Pool/SpawnGroup.cs
    Workspaces/WorkspaceManager.cs
    Sessions/SessionManager.cs
    Runtime/HostRuntime.cs
    Runtime/HostEnvironment.cs
    Runtime/HostProcess.cs
    Runtime/PortAllocator.cs
    Harnesses/OpenCode/OpenCodeHarness.cs
    Harnesses/OpenCode/OpenCodeClient.cs
    Harnesses/OpenCode/OpenCodeSession.cs
    Harnesses/OpenCode/SseStreamReader.cs
    Harnesses/ClaudeCode/ClaudeCodeHarness.cs
    Harnesses/ClaudeCode/ClaudeCodeSession.cs
    Harnesses/ClaudeCode/ClaudeCodePendingSession.cs
    Harnesses/ClaudeCode/NdjsonReader.cs
    Harnesses/ClaudeCode/EventTransformer.cs
    Resilience/CircuitBreakerPolicies.cs
    Telemetry/FleetMetrics.cs
    Telemetry/FleetActivitySource.cs
    DependencyInjection.cs
    
  WeaveFleet.Api/
    WeaveFleet.Api.csproj
    Program.cs
    appsettings.json
    appsettings.Development.json
    Hubs/FleetHub.cs
    Services/SignalRBridgeService.cs
    Services/PoolRecoveryService.cs
    Endpoints/SessionEndpoints.cs
    Endpoints/InstanceEndpoints.cs
    Endpoints/FleetEndpoints.cs
    Endpoints/DirectoryEndpoints.cs
    Endpoints/HarnessEndpoints.cs
    Endpoints/WorkspaceRootEndpoints.cs
    Endpoints/ConfigEndpoints.cs
    Endpoints/HealthEndpoints.cs
    Contracts/Sessions/CreateSessionRequest.cs
    Contracts/Sessions/SessionListItemResponse.cs
    Contracts/Sessions/SendPromptRequest.cs
    Contracts/Sessions/SendCommandRequest.cs
    Contracts/Instances/InstanceResponse.cs
    Contracts/Fleet/FleetStatusResponse.cs
    Middleware/RequestLoggingMiddleware.cs
    Middleware/ExceptionHandlingMiddleware.cs
    HealthChecks/DatabaseHealthCheck.cs
    HealthChecks/HarnessHealthCheck.cs
    Extensions/EndpointExtensions.cs
    Extensions/ObservabilityExtensions.cs
    Extensions/MiddlewareExtensions.cs
    
  WeaveFleet.Cli/
    WeaveFleet.Cli.csproj
    Program.cs
    Commands/ServeCommand.cs
    
tests/
  WeaveFleet.Domain.Tests/
    WeaveFleet.Domain.Tests.csproj
    Identifiers/SessionIdTests.cs
    Entities/SessionTests.cs
    
  WeaveFleet.Application.Tests/
    WeaveFleet.Application.Tests.csproj
    Pool/SpawnGroupTests.cs
    Pool/PoolManagerTests.cs
    Sessions/SessionManagerTests.cs
    
  WeaveFleet.Infrastructure.Tests/
    WeaveFleet.Infrastructure.Tests.csproj
    Persistence/FleetRepositoryTests.cs
    Messaging/MessageBusTests.cs
    Harnesses/OpenCode/SseStreamReaderTests.cs
    Harnesses/ClaudeCode/NdjsonReaderTests.cs
    
  WeaveFleet.Api.Tests/
    WeaveFleet.Api.Tests.csproj
    Endpoints/SessionEndpointsTests.cs
    Hubs/FleetHubTests.cs
```

**Total: ~150 files**

---

## Appendix: NuGet Package Reference

```xml
<!-- Core Framework -->
Microsoft.EntityFrameworkCore.Sqlite (10.0.0)
Microsoft.AspNetCore.SignalR (10.0.0)
System.CommandLine (2.0.0-beta5.*)

<!-- Observability -->
OpenTelemetry (1.10.0)
OpenTelemetry.Extensions.Hosting (1.10.0)
OpenTelemetry.Exporter.OpenTelemetryProtocol (1.10.0)
OpenTelemetry.Instrumentation.AspNetCore (1.10.0)
OpenTelemetry.Instrumentation.Http (1.10.0)

<!-- Resilience -->
Polly (8.5.0)
Polly.Extensions (8.5.0)

<!-- Testing -->
xunit (2.9.0)
xunit.runner.visualstudio (2.8.2)
FluentAssertions (7.0.0)
NSubstitute (5.3.0)
Microsoft.NET.Test.Sdk (17.11.1)
Microsoft.AspNetCore.Mvc.Testing (10.0.0)
Testcontainers (4.0.0)
```

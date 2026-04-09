/**
 * V1 API types — request/response shapes for the orchestrator API layer.
 * These are the types shared between API routes, React hooks, and UI components.
 */

import type {
  SessionActivityStatus,
  SessionLifecycleStatus,
  SessionRetentionStatus,
  InstanceStatus,
} from "@/lib/types";
import type { ContextSource } from "@/integrations/types";
import type {
  FleetPluginDescriptor,
  FleetPluginStatus,
} from "@/plugins/types";

// Re-export status types for consumer convenience
export type { SessionActivityStatus, SessionLifecycleStatus, SessionRetentionStatus, InstanceStatus };

/**
 * Fleet-owned session shape — contains only the fields the frontend needs.
 * This deliberately does NOT import from @opencode-ai/sdk to keep the frontend
 * harness-agnostic.
 */
export interface FleetSession {
  id: string;
  title: string;
  isHidden?: boolean;
  time: {
    created: number;
    updated: number;
  };
}

// ─── Request/Response Shapes ───────────────────────────────────────────────

export interface CreateSessionRequest {
  directory: string;
  title?: string;
  isolationStrategy?: "existing" | "worktree" | "clone";
  branch?: string;
  /** Optional integration context to inject as the initial prompt */
  context?: ContextSource;
  /** Optional pre-formatted initial prompt text (takes precedence over context) */
  initialPrompt?: string;
  /** Harness type to use for this session (e.g. "opencode", "claude"). Defaults to "opencode" on backend. */
  harnessType?: string;
  /** Optional project to assign this session to at creation time */
  projectId?: string;
  onComplete?: {
    /** OpenCode session ID of the conductor session to notify on completion */
    notifySessionId: string;
    /** Instance ID of the conductor (needed to get the SDK client) */
    notifyInstanceId: string;
  };
}

export interface CreateSessionResponse {
  instanceId: string;
  workspaceId: string;
  session: FleetSession;
}

export interface ResumeSessionResponse {
  instanceId: string;
  session: FleetSession;
}

export interface ForkSessionRequest {
  /** Optional title for the forked session. Defaults to "New Session". */
  title?: string;
}

export interface ForkSessionResponse {
  instanceId: string;
  workspaceId: string;
  session: FleetSession;
  /** The source session ID that was forked (Fleet DB id or opencode session id) */
  forkedFromSessionId: string;
}

export interface SendPromptRequest {
  instanceId: string;
  text: string;
  agent?: string;
  model?: { providerID: string; modelID: string };
  attachments?: ImageAttachment[];
}

/** An image attachment sent alongside a prompt (base64-encoded). */
export interface ImageAttachment {
  /** MIME type: image/png, image/jpeg, image/gif, image/webp */
  mime: string;
  /** Optional filename for display */
  filename?: string;
  /** Base64-encoded image data (NOT the full data URI — just the base64 payload) */
  data: string;
}

export interface SendCommandRequest {
  instanceId: string;
  command: string;
  args?: string;
  agent?: string;
  model?: { providerID: string; modelID: string };
}

export interface SendCommandResponse {
  success: boolean;
  sessionId: string;
}

// ─── Session List ──────────────────────────────────────────────────────────

export interface SessionListItem {
  instanceId: string;
  workspaceId: string;
  workspaceDirectory: string;
  workspaceDisplayName: string | null;
  isolationStrategy: string;
  sessionStatus: "active" | "idle" | "stopped" | "completed" | "disconnected" | "error" | "waiting_input";
  session: FleetSession;
  instanceStatus: "running" | "dead";
  /** Fleet DB session ID of the parent (conductor) session, if this is a child */
  parentSessionId?: string | null;
  /**
   * The original project directory this session was created from.
   * For worktree/clone sessions, this is the source project path (e.g. /Users/you/my-project).
   * For "existing" sessions or when DB is unavailable, this is null.
   */
  sourceDirectory: string | null;
  /**
   * The git branch this session's workspace was created on (worktree/clone isolation only).
   * Null for "existing" isolation or when workspace metadata is unavailable.
   */
  branch: string | null;
  /**
   * Activity status — what the session's agent is currently doing.
   * Only meaningful while lifecycleStatus is "running".
   */
  activityStatus: SessionActivityStatus | null;
  /**
   * Lifecycle status — overall terminal/non-terminal state of the session.
   */
  lifecycleStatus: SessionLifecycleStatus;
  /**
   * Retention status — whether the session is visible in active lists or archived.
   */
  retentionStatus: SessionRetentionStatus;
  /**
   * ISO timestamp when the session was archived, or null when active.
   */
  archivedAt: string | null;
  /**
   * Instance status — whether the OpenCode process backing this session is healthy.
   */
  typedInstanceStatus: InstanceStatus;
  isHidden: boolean;
  /**
   * Total token count across all messages (populated when available from SSE aggregation or DB).
   */
  totalTokens?: number;
  /**
   * Total cost in USD across all messages (populated when available).
   */
  totalCost?: number;
  /**
   * The Fleet project this session belongs to (null = unassigned / scratch project).
   */
  projectId?: string | null;
  /**
   * The display name of the project (populated when available).
   */
  projectName?: string | null;
}

// ─── Projects ──────────────────────────────────────────────────────────────

export interface ProjectResponse {
  id: string;
  name: string;
  description: string | null;
  /** "user" | "scratch" */
  type: string;
  position: number;
  sessionCount: number;
  createdAt: string;
  updatedAt: string;
}

export interface CreateProjectRequest {
  name: string;
  description?: string;
}

export interface UpdateProjectRequest {
  name?: string;
  description?: string;
}

export interface ReorderProjectRequest {
  position: number;
}

// ─── Streamed Event Model ──────────────────────────────────────────────────

/**
 * The simplified event model sent from the WebSocket to the browser.
 * Each event carries the raw SDK event type + properties for the client
 * to handle — we avoid mapping here to stay close to the SDK source of truth.
 */
export interface WebSocketEvent {
  type: string;
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  properties: Record<string, any>;
}

export interface DelegationDto {
  delegationId: string;
  parentToolCallId: string | null;
  childSessionId: string | null;
  title: string;
  status: "pending" | "running" | "completed" | "error" | "cancelled";
}

// ─── Accumulated Message (for useSessionEvents) ────────────────────────────

export interface AccumulatedTextPart {
  partId: string;
  type: "text";
  text: string;
}

export interface AccumulatedToolPart {
  partId: string;
  type: "tool";
  tool: string;
  callId: string;
  state: unknown;
}

export interface AccumulatedFilePart {
  partId: string;
  type: "file";
  mime: string;
  filename?: string;
  /** Full data URI or URL for rendering */
  url: string;
}

export type AccumulatedPart = AccumulatedTextPart | AccumulatedToolPart | AccumulatedFilePart;

export interface AccumulatedMessage {
  messageId: string;
  sessionId: string;
  role: "user" | "assistant";
  parts: AccumulatedPart[];
  /** Cost in USD — populated from step-finish parts */
  cost?: number;
  tokens?: { input: number; output: number; reasoning: number };
  /** ISO timestamp */
  createdAt?: number;
  /** The agent name — sourced from info.agent for both user and assistant messages (v2) */
  agent?: string;
  modelID?: string;
  completedAt?: number;
  parentID?: string;
}

// ─── Autocomplete Types ─────────────────────────────────────────────────────

/** Slim command shape returned by GET /api/instances/[id]/commands */
export interface AutocompleteCommand {
  name: string;
  description?: string;
}

/** Slim agent shape returned by GET /api/instances/[id]/agents */
export interface AutocompleteAgent {
  name: string;
  description?: string;
  mode: string;
  color?: string;
  model?: { modelID: string; providerID: string };
  hidden?: boolean;
}

// ─── Model Picker Types ────────────────────────────────────────────────────

/** A single model within a connected provider */
export interface AvailableModel {
  id: string;    // e.g. "claude-sonnet-4-5"
  name: string;  // e.g. "Claude Sonnet 4.5"
}

/** A connected provider with its available models — returned by GET /api/instances/[id]/models */
export interface AvailableProvider {
  id: string;      // e.g. "anthropic"
  name: string;    // e.g. "Anthropic"
  models: AvailableModel[];
}

// File search returns Array<string> (file paths) — no wrapper type needed

// ─── Directory Browser Types ────────────────────────────────────────────────

/** A single directory entry returned by GET /api/directories */
export interface DirectoryEntry {
  /** Directory name, e.g. "my-project" */
  name: string;
  /** Absolute path, e.g. "/home/user/my-project" */
  path: string;
  /** True if the directory contains a .git subdirectory */
  isGitRepo: boolean;
}

/** Response shape for GET /api/directories */
export interface DirectoryListResponse {
  /** Subdirectories in the listed path */
  entries: DirectoryEntry[];
  /** The resolved absolute path being listed */
  currentPath: string;
  /** Parent directory path, or null if at an allowed root */
  parentPath: string | null;
  /** The allowed workspace roots (for root-level navigation) */
  roots: string[];
}

// ─── Fleet Summary ──────────────────────────────────────────────────────────

export interface FleetSummaryResponse {
  activeSessions: number;
  idleSessions: number;
  totalTokens: number;
  totalCost: number;
  queuedTasks: number;
}

// ─── Diff Types ─────────────────────────────────────────────────────────────

/** Mirrors the SDK's FileDiff shape for frontend consumption (decouples frontend from SDK types) */
export interface FileDiffItem {
  file: string;
  before: string;
  after: string;
  additions: number;
  deletions: number;
  status: "added" | "deleted" | "modified";
}

// ─── Harness Types ─────────────────────────────────────────────────────────

/** Capabilities declared by a harness — drives adaptive UI. */
export interface HarnessCapabilities {
  requiresInitialPrompt: boolean;
  supportsAgents: boolean;
  supportsModelSelection: boolean;
  supportsCommands: boolean;
  supportsForking: boolean;
  supportsResume: boolean;
  supportsImageAttachments: boolean;
  supportsStreaming: boolean;
  supportsDelegation: boolean;
}

/** Information about a registered harness */
export interface HarnessInfo {
  /** Machine-readable harness type, e.g. "opencode" or "claude-code" */
  type: string;
  /** Human-readable display name, e.g. "OpenCode" or "Claude Code" */
  displayName: string;
  /** Whether the harness is currently available (binary found, auth configured, etc.) */
  available: boolean;
  /** Human-readable reason if unavailable */
  reason?: string;
  /** Capabilities this harness supports */
  capabilities: HarnessCapabilities;
}

// ─── Analytics Types ──────────────────────────────────────────────────────

export interface AnalyticsSummary {
  totalTokens: number;
  totalCost: number;
  totalEstimatedCost: number;
  sessionCount: number;
  messageCount: number;
  topModels: AnalyticsTopItem[];
  topProjects: AnalyticsTopItem[];
}

export interface AnalyticsTopItem {
  name: string;
  tokens: number;
  cost: number;
}

export interface DailyAnalytics {
  date: string;          // ISO date string "YYYY-MM-DD"
  tokens: number;
  cost: number;
  estimatedCost: number;
  sessions: number;
  messages: number;
}

export interface SessionAnalytics {
  sessionId: string;
  title: string | null;
  projectId: string | null;
  projectName: string | null;
  tokens: number;
  cost: number;
  estimatedCost: number;
  models: string[];
  durationSeconds: number | null;
  createdAt: string;     // ISO datetime
}

export interface ModelAnalytics {
  modelId: string;
  providerId: string;
  tokens: number;
  cost: number;
  estimatedCost: number;
  messageCount: number;
  avgCostPerMessage: number;
}

// ─── Workspace Roots Types ──────────────────────────────────────────────────/** A single workspace root returned by GET /api/workspace-roots */
export interface WorkspaceRootItem {
  /** DB id, or null for env-var roots */
  id: string | null;
  /** Absolute path to the workspace root */
  path: string;
  /** Whether this root comes from the env var ("env") or was user-added ("user") */
  source: "env" | "user";
  /** Whether the path currently exists on the filesystem */
  exists: boolean;
}

/** Response shape for GET /api/workspace-roots */
export interface WorkspaceRootsResponse {
  roots: WorkspaceRootItem[];
}

/** Request body for POST /api/workspace-roots */
export interface AddWorkspaceRootRequest {
  path: string;
}

/** Response shape for POST /api/workspace-roots */
export interface AddWorkspaceRootResponse {
  id: string;
  path: string;
}

// ─── Repository Scanner Types ────────────────────────────────────────────────

export interface ScannedRepository {
  name: string;        // directory name, e.g. "my-project"
  path: string;        // absolute path, e.g. "/home/user/repos/my-project"
  parentRoot: string;  // the workspace root it was found under
}

export interface RepositoryScanResponse {
  repositories: ScannedRepository[];
  scannedAt: number;   // unix timestamp (ms) of last scan
}

export interface RepositoryInfo {
  name: string;
  path: string;
  branch: string | null;
  lastCommit: {
    hash: string;
    message: string;
    author: string;
    date: string;      // ISO timestamp
  } | null;
  remotes: Array<{ name: string; url: string }>;
}

export interface RepositoryInfoResponse {
  repository: RepositoryInfo;
}

// ─── Repository Detail Types (enriched) ──────────────────────────────────────

export interface BranchInfo {
  name: string;           // e.g. "main", "remotes/origin/feature-x"
  shortHash: string;      // abbreviated commit hash
  message: string;        // latest commit subject
  author: string;         // author name
  authorEmail: string;    // author email (for gravatar)
  date: string;           // ISO timestamp of latest commit
  isCurrent: boolean;     // true if this is the HEAD branch
  isRemote: boolean;      // true if starts with "remotes/" or "origin/"
}

export interface TagInfo {
  name: string;           // e.g. "v1.0.0"
  shortHash: string;      // abbreviated object hash
  date: string;           // ISO timestamp (creator date)
  tagger: string;         // tagger name (empty string for lightweight tags)
  taggerEmail: string;    // tagger email (empty string for lightweight tags)
}

export interface CommitInfo {
  hash: string;           // full SHA
  shortHash: string;      // abbreviated SHA
  message: string;        // subject line
  author: string;         // author name
  authorEmail: string;    // author email
  date: string;           // ISO timestamp
}

export interface GitHubRemoteInfo {
  owner: string;
  repo: string;
  repoUrl: string;        // https://github.com/owner/repo
  issuesUrl: string;      // https://github.com/owner/repo/issues
  pullsUrl: string;       // https://github.com/owner/repo/pulls
}

export interface RemoteInfo {
  name: string;           // e.g. "origin"
  url: string;            // raw URL
  github: GitHubRemoteInfo | null; // parsed GitHub info, null if not GitHub
}

export interface RepositoryDetail {
  name: string;
  path: string;
  branch: string | null;          // current HEAD branch
  uncommittedCount: number;       // number of uncommitted files (from git status)
  totalCommitCount: number;       // total commits on HEAD
  firstCommitDate: string | null; // ISO timestamp of initial commit
  lastCommitDate: string | null;  // ISO timestamp of most recent commit
  branches: BranchInfo[];         // all branches sorted by committer date desc
  tags: TagInfo[];                // all tags sorted by creator date desc
  recentCommits: CommitInfo[];    // last 10 commits
  remotes: RemoteInfo[];          // remotes with parsed GitHub info
  readmeContent: string | null;   // raw README text, null if not found
  readmeFilename: string | null;  // actual filename found (e.g. "README.md")
}

export interface RepositoryDetailResponse {
  repository: RepositoryDetail;
}

// ─── Session History Types ──────────────────────────────────────────────────

export interface HistorySession {
  id: string;
  harnessSessionId: string | null;
  instanceId: string;
  title: string | null;
  status: string;
   retentionStatus: SessionRetentionStatus;
  directory: string;
  workspaceDisplayName: string | null;
  createdAt: string;
  stoppedAt: string | null;
   archivedAt: string | null;
}

export interface UpdateSessionRetentionRequest {
  retentionStatus: SessionRetentionStatus;
}

export interface HistoryResponse {
  sessions: HistorySession[];
  total: number;
}

// ─── Integration Types ──────────────────────────────────────────────────────

export interface IntegrationStatusInfo {
  id: string;
  name: string;
  status: "connected" | "disconnected" | "error";
  connectedAt?: string;
}

export interface PluginCatalogResponse {
  plugins: FleetPluginDescriptor[];
  statuses: FleetPluginStatus[];
}

// ─── GitHub Device Authorization Flow Types (RFC 8628) ─────────────────────

/**
 * Response from POST /api/integrations/github/auth/device-code.
 * Contains the user-facing code and URL to display, plus the opaque
 * device_code the client must echo back when polling.
 */
export interface DeviceCodeResponse {
  /** The user-facing code to enter at verificationUri (e.g. "WDJB-MJHT") */
  userCode: string;
  /** The URL the user should navigate to (https://github.com/login/device) */
  verificationUri: string;
  /** Opaque code passed back to /poll. Safe to hold on the client — useless without server-side client_id. */
  deviceCode: string;
  /** Seconds until the device code expires (typically 900 = 15 minutes) */
  expiresIn: number;
  /** Minimum polling interval in seconds (typically 5) */
  interval: number;
}

/**
 * Request body for POST /api/integrations/github/auth/poll.
 */
export interface PollRequest {
  /** The opaque device_code returned by the device-code route */
  deviceCode: string;
}

/**
 * Response from POST /api/integrations/github/auth/poll.
 */
export interface PollResponse {
  /** Current authorization status */
  status: "pending" | "complete" | "expired" | "denied" | "error";
  /**
   * Updated polling interval in seconds — only present on slow_down errors.
   * The client must increase its interval to this value.
   */
  interval?: number;
  /** Human-readable message for terminal/error states */
  message?: string;
}

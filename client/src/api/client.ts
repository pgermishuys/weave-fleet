import createClient from "openapi-fetch";
import type { paths } from "./generated/schema";
import type { SessionProgressSummary } from "@/lib/session-progress";
import { apiUrl, setApiBase } from "@/lib/api-client";
import { getActiveMachine, machineRequestInit } from "@/lib/machines";

/**
 * Typed API client for Weave Fleet API
 * 
 * Usage:
 * ```ts
 * import { api } from '@/api/client';
 * 
 * // Get all sessions
 * const { data, error } = await api.GET("/api/sessions");
 * 
 * // Create a new session
 * const { data, error } = await api.POST("/api/sessions", {
 *   body: {
 *     workspaceId: "default",
 *     title: "New session"
 *   }
 * });
 * ```
 */

const csrfCookieName = ".WeaveFleet.CSRF";

/**
 * `setApiBase` lives with the other request paths in `@/lib/api-client`; it is re-exported here for callers
 * that already import it from this module.
 */
export { setApiBase };

function getCookieValue(name: string): string | null {
  if (typeof document === "undefined") {
    return null;
  }

  const cookies = document.cookie.split(";");
  for (const cookie of cookies) {
    const [rawName, ...rawValue] = cookie.trim().split("=");
    if (rawName === name) {
      return decodeURIComponent(rawValue.join("="));
    }
  }

  return null;
}

/**
 * The client builds every request with an empty base, so it gets `/api/...`. This resolves that path against
 * the machine the app is working in when the request is made, the same way `apiFetch` does. Resolving it once,
 * when the client was created, is what left `setApiBase` without effect on these calls.
 */
const RequestOnLiveMachine = (typeof Request === "undefined"
  ? undefined
  : class extends Request {
    constructor(input: RequestInfo | URL, init?: RequestInit) {
      super(typeof input === "string" ? resolveOnPage(apiUrl(input)) : input, init);
    }
  }) as typeof Request | undefined;

/** A same-origin path as the full URL a browser would make of it; runtimes without a page need it spelled out. */
function resolveOnPage(url: string): string {
  const page = typeof window === "undefined" ? undefined : window.location?.href;
  return page ? new URL(url, page).href : url;
}

/**
 * Custom fetch implementation that:
 * - Attaches CSRF token to mutating requests on the home machine
 * - Sends cookies to the home machine, and another machine's token (never cookies) to that machine
 */
const customFetch: typeof fetch = (input, init) => {
  // openapi-fetch passes a Request object as `input` with headers already set.
  // Merge headers from both the Request and any init overrides.
  const requestHeaders = input instanceof Request ? input.headers : new Headers();
  const initHeaders = new Headers(init?.headers);
  const headers = new Headers(requestHeaders);

  // Layer init headers on top (overrides request headers)
  initHeaders.forEach((value, key) => {
    headers.set(key, value);
  });

  const machine = getActiveMachine();
  const method = (init?.method ?? (input instanceof Request ? input.method : "GET")).toUpperCase();

  // Attach CSRF token to mutating requests
  if (!machine && !["GET", "HEAD", "OPTIONS", "TRACE"].includes(method)) {
    const csrfToken = getCookieValue(csrfCookieName);
    if (csrfToken) {
      headers.set("X-CSRF-Token", csrfToken);
    }
  }

  return fetch(input, machineRequestInit(machine, { ...init, headers }));
};

export const api = createClient<paths>({
  baseUrl: "",
  fetch: customFetch,
  ...(RequestOnLiveMachine ? { Request: RequestOnLiveMachine } : {}),
});

export type { paths, components } from "./generated/schema";

// ─── Schema Type Re-exports ─────────────────────────────────────────────────
// Convenient aliases for commonly-used OpenAPI schema types

import type { components } from "./generated/schema";

export type CreateSessionRequest = components["schemas"]["CreateSessionApiRequest"];

export interface CreateSessionResponse {
  instanceId: string;
  workspaceId: string;
  session: FleetSession;
  /** The branch the server gave a new worktree, which the naming templates decided. */
  branch?: string | null;
}

export type ClientConfigResponse = components["schemas"]["ClientConfigResponse"];
export type UserMeResponse = components["schemas"]["UserMeResponse"];
export type AddSessionSourceRequest = components["schemas"]["AddSessionSourceApiRequest"];
export type ForkSessionRequest = components["schemas"]["ForkSessionApiRequest"];
export type SendPromptRequest = components["schemas"]["SendPromptApiRequest"];
export type SendCommandRequest = components["schemas"]["SendCommandApiRequest"];
export type SessionOrigin = components["schemas"]["SessionOriginDto"];
export type SessionProvenanceRecord = components["schemas"]["SessionOriginRecordDto"];
export type CreateProjectRequest = components["schemas"]["CreateProjectRequest"];
export type UpdateProjectRequest = components["schemas"]["UpdateProjectRequest"];
export type ReorderProjectRequest = components["schemas"]["ReorderProjectRequest"];
export type AddWorkspaceRootRequest = components["schemas"]["AddWorkspaceRootRequest"];
export type UpdateSessionRetentionRequest = components["schemas"]["UpdateSessionRetentionRequest"];
export type CredentialSummary = components["schemas"]["CredentialResponse"];
export type FleetSession = Omit<components["schemas"]["SessionFleetInfo"], "tags"> & {
  tags: readonly string[];
};
export type SessionSourceKey = components["schemas"]["SessionSourceKey"];
export type SessionSourceSelection = components["schemas"]["SessionSourceSelection"];
export type SessionActionCapabilities = components["schemas"]["SessionActionCapabilities"];

// ─── Manual Type Definitions ────────────────────────────────────────────────
// These types are not in the OpenAPI schema (endpoints return content?: never)
// or have different shapes than the schema. They should eventually be added to
// the OpenAPI spec.
//
// NOTE: Some types override the generated schema types because the OpenAPI
// generator produces `number | string` for numeric fields, but the API actually
// returns numbers and the frontend expects numbers.

export interface SessionSourceInputField {
  name: string;
  valueType: string;
  required: boolean;
  allowedValues: string[] | null;
  description: string | null;
}

export interface SessionSourceDescriptor {
  key: SessionSourceKey;
  displayName: string;
  kind: "workspace" | "context" | "hybrid";
  inputFields: SessionSourceInputField[];
  producesWorkspace: boolean;
  producesContext: boolean;
  requiresConfirmation: boolean;
}

export interface SessionSourceCatalogResponse {
  sources: SessionSourceDescriptor[];
}

export interface SessionSourcePreview {
  originLabel: string;
  content: string;
  isTruncated: boolean;
  characterCount: number;
}

export interface PreviewSessionSourceResponse {
  preview: SessionSourcePreview;
}

export interface PreviewSessionSourceRequest {
  source: SessionSourceSelection;
}

export interface ForkSessionResponse {
  instanceId: string;
  workspaceId: string;
  session: FleetSession;
  forkedFromSessionId: string;
}

export interface SendCommandResponse {
  success: boolean;
  sessionId: string;
}

export interface AutocompleteCommand {
  name: string;
  description?: string;
}

export interface AutocompleteAgent {
  name: string;
  description?: string;
  mode: string;
  color?: string;
  model?: { modelID: string; providerID: string };
  hidden?: boolean;
}

export interface AvailableModel {
  id: string;
  name: string;
  variants?: string[];
}

export interface AvailableProvider {
  id: string;
  name: string;
  models: AvailableModel[];
}

/** A model as `{ providerID, modelID }`, the way the harness names one. */
export interface ModelReference {
  providerID: string;
  modelID: string;
}

/**
 * What a harness offers in a folder before a session exists there (`GET /api/harnesses/{type}/catalog`).
 * `supported` is false when the harness can't say without a session; the lists are then empty.
 */
export interface HarnessCatalog {
  supported: boolean;
  agents: AutocompleteAgent[];
  providers: AvailableProvider[];
  /** The agent a prompt goes to when it names none. */
  defaultAgent: string | null;
  /** The model a prompt gets when neither it nor its agent names one; null when only the harness knows. */
  defaultModel: ModelReference | null;
}

export interface DirectoryEntry {
  name: string;
  path: string;
  isGitRepo: boolean;
}

export interface DirectoryListResponse {
  entries: DirectoryEntry[];
  currentPath: string | null;
  parentPath: string | null;
  roots: string[];
}

/** One folder as the new-session folder picker sees it. */
export interface FolderInspection {
  path: string;
  exists: boolean;
  isGitRepo: boolean;
  isWithinRoots: boolean;
}

export interface FileDiffItem {
  file: string;
  before: string;
  after: string;
  additions: number;
  deletions: number;
  status: "added" | "deleted" | "modified";
  isBinary?: boolean;
  isTruncated?: boolean;
  binary?: boolean;
  truncated?: boolean;
}

export interface SessionDiffsResponse {
  diffs: FileDiffItem[];
  available: boolean;
}

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
  /** Sessions can start with a profile: harness config kept in Fleet and picked per session. */
  supportsProfiles?: boolean;
  /** Fleet can sign in to the harness's providers (`/api/harnesses/{type}/sign-in`). Off when Fleet runs with sign-in. */
  supportsProviderSignIn?: boolean;
  /** Sessions can be workflow steps: the harness hides the step tool from every other session. */
  supportsWorkflowSteps?: boolean;
  /** The harness can ask about a session off the record, leaving its history alone: recaps and Save as workflow…. */
  supportsOffTheRecordPrompt?: boolean;
  /** A message sent while a turn runs can go into that turn (steer) instead of waiting in the queue. */
  supportsSteering?: boolean;
  /** The user can run a shell command in the session's folder from the composer (`!git status`). */
  supportsShellCommands?: boolean;
  /** A session can fork into a side conversation at its last finished turn (`/btw` in the composer). */
  supportsSideConversations?: boolean;
}

/** A field a sign-in method asks for besides the key or browser (`HarnessSignInField` on the server). */
export interface HarnessSignInField {
  key: string;
  /** `string` (a choice when it has options), `boolean`, `number`, `integer`, `multiselect` or `external` (a link). */
  type: string;
  title?: string | null;
  description?: string | null;
  required: boolean;
  /** Not asked; its default is sent. */
  hidden: boolean;
  placeholder?: string | null;
  default?: unknown;
  options?: { value: string; label: string; description?: string | null }[] | null;
  /** Asked only when every condition holds: field `key`'s answer is (`eq`) or isn't (`neq`) `value`. */
  when?: { key: string; op: "eq" | "neq"; value: unknown }[] | null;
  url?: string | null;
}

/** One way to sign in to a provider. */
export interface HarnessSignInMethod {
  type: "key" | "oauth" | "command" | "env";
  /** Names an `oauth` or `command` method. */
  id?: string | null;
  label: string;
  fields: HarnessSignInField[];
  /** For `command`: what the harness would run. Fleet doesn't run it. */
  command?: string[] | null;
  /** For `env`: the variables the harness reads. */
  environmentVariables?: string[] | null;
}

/** A sign-in a provider has: one the harness keeps (`credential`) or a variable in its environment (`env`). */
export interface HarnessSignInConnection {
  kind: "credential" | "env";
  id: string;
  label: string;
  /** The one the provider uses. */
  active: boolean;
}

export interface HarnessSignInProvider {
  id: string;
  name: string;
  methods: HarnessSignInMethod[];
  /** The one in use first. */
  connections: HarnessSignInConnection[];
}

/** `GET /api/harnesses/{type}/sign-in`. */
export interface HarnessSignIns {
  providers: HarnessSignInProvider[];
  /** Where the harness keeps its sign-ins, and what signing in here changes. */
  note?: string | null;
}

/** A browser sign-in the harness started. */
export interface HarnessSignInAttempt {
  id: string;
  /** The provider's sign-in page. */
  url: string;
  /** What to do there; may carry a code to enter. */
  instructions: string;
  /** The provider shows a code to paste back. */
  needsCode: boolean;
  expiresAt: string;
  /**
   * The provider sends the browser back to this address on the computer Fleet runs on. A browser on another device
   * can't open it; the address it landed on can be pasted back instead.
   */
  callbackAddress?: string | null;
}

export type HarnessSignInState = "pending" | "complete" | "failed" | "expired" | "gone";

export interface HarnessSignInAttemptStatus {
  status: HarnessSignInState;
  message?: string | null;
}

/** A harness profile, as `GET /api/harnesses/{type}/profiles` lists it. */
export interface HarnessProfile {
  id: string;
  harnessType: string;
  name: string;
  /** The config itself; for OpenCode, an opencode.json layered over the user's own. */
  content: string;
  isDefault: boolean;
  /** Sessions that use it and aren't archived. */
  openSessions: number;
  createdAt: string;
  updatedAt: string;
}

/** What the harness said when it tried a profile. */
export interface HarnessProfileCheck {
  ok: boolean;
  error?: string | null;
  details?: string[] | null;
}

/** Which Weave a harness loaded: Weave (`config.weave`) or Weave Legacy (`weave-opencode.jsonc`). */
export type WeaveFlavor = "weave" | "legacy";

/** A Weave plugin a harness loaded. */
export interface WeaveInstall {
  flavor: WeaveFlavor;
  package: string;
  /** The plugin as the harness listed it: a package with its version, or a `file://` path. */
  entry: string;
  /** Whether this version reads the folder Fleet points it at (Weave 0.2.0-next.1+, Legacy 0.9.0+). */
  acceptsFleetConfig: boolean;
}

/** What Fleet found in one harness. `checked` is false when Fleet can't hand it a config; `note` says why. */
export interface WeaveHarnessDetection {
  harnessType: string;
  harnessName: string;
  checked: boolean;
  installs: WeaveInstall[];
  note?: string | null;
}

/** What a harness loaded when it tried a draft. */
export interface WeaveCheck {
  ok: boolean;
  agents: string[];
  error?: string | null;
  details?: string[] | null;
}

export interface WeaveHarnessCheck {
  harnessType: string;
  harnessName: string;
  flavor: WeaveFlavor;
  check: WeaveCheck;
}

/** How far a save has got in running processes: each folder reloads once nothing is running in it. */
export interface WeaveApplyStatus {
  folders: { directory: string; reloaded: boolean }[];
  error?: string | null;
}

/** `GET /api/weave`: which Weave each harness loads, and the config Fleet keeps (files by path in its folder). */
export interface WeaveConfigView {
  source: "own" | "fleet";
  files: Record<string, string>;
  updatedAt?: string | null;
  harnesses: WeaveHarnessDetection[];
  apply?: WeaveApplyStatus | null;
}

/** `PUT /api/weave`: when a check failed nothing was saved, and `config` is null. */
export interface WeaveSaveResult {
  saved: boolean;
  checks: WeaveHarnessCheck[];
  config?: WeaveConfigView | null;
}

/** `GET /api/weave/own`: the user's own Weave files. */
export interface WeaveOwnConfig {
  flavor: WeaveFlavor;
  path: string;
  files: Record<string, string>;
}

/** Sent as `harnessProfileId` to start a session without a profile, even when there's a default. */
export const NO_PROFILE = "none";

/** What a harness needs before sessions can use it (`HarnessStates` on the server). */
export type HarnessState = "ready" | "not-installed" | "sign-in-required" | "not-working" | "update-needed";

/** An update Fleet started for a harness (`HarnessUpdateJob` on the server). */
export interface HarnessUpdateJob {
  phase: "waiting" | "running" | "succeeded" | "failed";
  /** What happened, in a sentence; missing while running. */
  message?: string | null;
  /** The last lines the updater printed. */
  output?: string | null;
  /** While waiting: sessions still working. */
  workingSessions: number;
  fromVersion?: string | null;
  toVersion?: string | null;
}

/** A harness's latest version and any update Fleet is running. Missing in cloud mode. */
export interface HarnessUpdateInfo {
  latestVersion?: string | null;
  updateAvailable: boolean;
  minimumVersion?: string | null;
  /** The update command as you'd type it. */
  command?: string | null;
  job?: HarnessUpdateJob | null;
}

/** How to install a harness or sign in to it on the machine Fleet runs on. Fleet types these; the user runs them. */
export interface HarnessSetup {
  installCommand?: string | null;
  signInCommand?: string | null;
  docsUrl?: string | null;
  /** Where to download it by hand, when there's no installer to type on this platform. */
  downloadUrl?: string | null;
  /** How it's (or will be) installed here, in a few words, for a harness with more than one way. */
  mode?: string | null;
  /** The folders the install uses: program, settings, data. */
  folders?: readonly HarnessFolder[] | null;
  /** What to know about this install, one sentence each. */
  notes?: readonly string[] | null;
  /** Where the user can install it, when there's more than one place to pick; the recommended one first. */
  installChoices?: readonly HarnessInstallChoice[] | null;
}

export interface HarnessFolder {
  label: string;
  path: string;
}

/** One place a harness can be installed, with what picking it means. */
export interface HarnessInstallChoice {
  id: string;
  label: string;
  description: string;
  command: string;
  folders: readonly HarnessFolder[];
  recommended: boolean;
}

export interface HarnessInfo {
  type: string;
  displayName: string;
  available: boolean;
  userEnabled: boolean;
  /** Why it isn't available, in a sentence the user can act on. */
  reason?: string | null;
  capabilities: HarnessCapabilities;
  state: HarnessState;
  /** The version the harness executable reports. */
  version?: string | null;
  /** Where Fleet found the harness executable. */
  executablePath?: string | null;
  /** How to install it or sign in to it here; missing when Fleet can't help. */
  setup?: HarnessSetup | null;
  update?: HarnessUpdateInfo | null;
}

export interface WorkspaceRootItem {
  id: string | null;
  path: string;
  source: "env" | "user";
  exists: boolean;
}

export interface WorkspaceRootsResponse {
  roots: WorkspaceRootItem[];
}

export interface AddWorkspaceRootResponse {
  id: string;
  path: string;
}

export interface ScannedRepository {
  name: string;
  path: string;
  parentRoot: string;
}

export interface RepositoryScanResponse {
  repositories: ScannedRepository[];
  scannedAt: number;
}

export interface WorktreeInfo {
  path: string;
  branch: string | null;
  commitHash: string | null;
}

export interface RepositoryWorktreesResponse {
  worktrees: WorktreeInfo[];
}

export interface RepositoryInfo {
  name: string;
  path: string;
  branch: string | null;
  lastCommit: {
    hash: string;
    message: string;
    author: string;
    date: string;
  } | null;
  remotes: Array<{ name: string; url: string }>;
}

export interface RepositoryInfoResponse {
  repository: RepositoryInfo;
}

export interface BranchInfo {
  name: string;
  shortHash: string;
  message: string;
  author: string;
  authorEmail: string;
  date: string;
  isCurrent: boolean;
  isRemote: boolean;
}

export interface TagInfo {
  name: string;
  shortHash: string;
  date: string;
  tagger: string;
  taggerEmail: string;
}

export interface CommitInfo {
  hash: string;
  shortHash: string;
  message: string;
  author: string;
  authorEmail: string;
  date: string;
}

export interface GitHubRemoteInfo {
  owner: string;
  repo: string;
  repoUrl: string;
  issuesUrl: string;
  pullsUrl: string;
}

export interface RemoteInfo {
  name: string;
  url: string;
  github: GitHubRemoteInfo | null;
}

export interface RepositoryDetail {
  name: string;
  path: string;
  branch: string | null;
  uncommittedCount: number;
  totalCommitCount: number;
  firstCommitDate: string | null;
  lastCommitDate: string | null;
  branches: BranchInfo[];
  tags: TagInfo[];
  recentCommits: CommitInfo[];
  remotes: RemoteInfo[];
  readmeContent: string | null;
  readmeFilename: string | null;
  /** The default branch, e.g. `main`; null when there's neither an origin default nor a local main or master. */
  defaultBranch: string | null;
  /** Where a new worktree starts when no base is chosen, e.g. `origin/main`; null means the current HEAD. */
  defaultBase: string | null;
}

export interface RepositoryDetailResponse {
  repository: RepositoryDetail;
}

export interface HistorySession {
  id: string;
  harnessSessionId: string | null;
  instanceId: string;
  title: string | null;
  status: string;
  retentionStatus: "active" | "archived";
  directory: string;
  workspaceDisplayName: string | null;
  createdAt: string;
  stoppedAt: string | null;
  archivedAt: string | null;
}

export interface HistoryResponse {
  sessions: HistorySession[];
  total: number;
}

export interface IntegrationStatusInfo {
  id: string;
  name: string;
  status: "connected" | "disconnected" | "error";
  connectedAt?: string;
}

export interface PluginCatalogResponse {
  plugins: Array<{
    id: string;
    name: string;
    version: string;
    description?: string;
  }>;
  statuses: Array<{
    pluginId: string;
    status: "loaded" | "error" | "disabled";
    error?: string;
  }>;
}

export interface DeviceCodeResponse {
  userCode: string;
  verificationUri: string;
  deviceCode: string;
  expiresIn: number;
  interval: number;
}

export interface PollRequest {
  deviceCode: string;
}

export interface PollResponse {
  status: "pending" | "complete" | "expired" | "denied" | "error";
  interval?: number;
  message?: string;
}

// ─── Type Overrides for Numeric Fields ─────────────────────────────────────
// The OpenAPI generator produces `number | string` for numeric fields, but the
// API actually returns numbers. These overrides ensure type safety in the frontend.

// ─── Type Overrides for Enums ──────────────────────────────────────────────
// The OpenAPI generator produces `number` for enums, but the API serializes them
// as strings. These overrides ensure type safety in the frontend.

export type SkillSource = "GitHub" | "Local" | "Bundled";

export interface SessionListItem {
  instanceId: string;
  workspaceId: string;
  workspaceDirectory: string;
  workspaceDisplayName: string | null;
  isolationStrategy: string;
  sessionStatus: string;
  session: FleetSession;
  instanceStatus: string;
  parentSessionId?: string | null;
  sourceDirectory?: string | null;
  branch?: string | null;
  activityStatus?: string | null;
  /** Client-only: set from the activity_status push while the harness retries. */
  retryAttempt?: number | null;
  lifecycleStatus: string;
  retentionStatus: string;
  archivedAt?: string | null;
  typedInstanceStatus: string;
  isHidden: boolean;
  totalTokens?: number | null;
  totalCost?: number | null;
  projectId?: string | null;
  projectName?: string | null;
  harnessType?: string | null;
  capabilities?: SessionActionCapabilities;
  /** The workflow run this session is a step of; the list nests it under the run. */
  workflowRunId?: string | null;
  origin?: SessionOrigin | null;
  tags: readonly string[];
  /** How far along the session is, when the server has seen a todo list for it. */
  progress?: SessionProgressSummary | null;
  /** The agent a prompt that names none goes to; null for the harness's default. */
  selectedAgent?: string | null;
  /** The model a prompt that names none gets; null for the agent's or the harness's default. */
  selectedModel?: ModelReference | null;
  /** Client-only: the model that answered last, from the open stream — what the header names when nothing is chosen. */
  lastAssistantModelId?: string | null;
}

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
  date: string;
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
  createdAt: string;
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

export interface FleetSummaryResponse {
  activeSessions: number;
  idleSessions: number;
  totalTokens: number;
  totalCost: number;
  queuedTasks: number;
}

export interface StoreCredentialRequest {
  label: string;
  namespace: string;
  kind: string;
  value: string;
  metadata: string | null;
}

export interface ProjectResponse {
  id: string;
  name: string;
  description: string | null;
  type: string;
  position: number;
  sessionCount: number;
  createdAt: string;
  updatedAt: string;
}

// ─── Session File Browser Types (re-exported from generated schema) ────────

export type BrowseDirectoryEntry = components["schemas"]["BrowseEntryDto"];
export type BrowseDirectoryResponse = components["schemas"]["BrowseSessionDirectoryResponse"];
export type FileContentResponse = components["schemas"]["ReadSessionFileResponse"];

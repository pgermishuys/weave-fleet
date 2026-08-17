import createClient from "openapi-fetch";
import type { paths } from "./generated/schema";

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
 * Runtime-configurable base URL (overrides all other sources when set)
 */
let runtimeBase: string | null = null;

/**
 * Override the API base URL at runtime.
 * Useful for multi-backend scenarios or testing.
 */
export function setApiBase(url: string): void {
  runtimeBase = url.replace(/\/$/, "");
}

function getApiBase(): string {
  if (runtimeBase !== null) return runtimeBase;
  // Check window global (can be injected by backend via <script> tag)
  if (typeof window !== "undefined" && (window as { __WEAVE_API_BASE__?: string }).__WEAVE_API_BASE__) {
    return ((window as { __WEAVE_API_BASE__?: string }).__WEAVE_API_BASE__ as string).replace(/\/$/, "");
  }
  // Fallback to build-time env var
  return (import.meta.env.VITE_API_BASE_URL ?? "").replace(/\/$/, "");
}

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
 * Custom fetch implementation that:
 * - Attaches CSRF token to mutating requests
 * - Sets credentials: "include" on all requests
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

  const method = (init?.method ?? (input instanceof Request ? input.method : "GET")).toUpperCase();

  // Attach CSRF token to mutating requests
  if (!["GET", "HEAD", "OPTIONS", "TRACE"].includes(method)) {
    const csrfToken = getCookieValue(csrfCookieName);
    if (csrfToken) {
      headers.set("X-CSRF-Token", csrfToken);
    }
  }

  return fetch(input, {
    ...init,
    credentials: init?.credentials ?? "include",
    headers,
  });
};

export const api = createClient<paths>({
  baseUrl: getApiBase(),
  fetch: customFetch,
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
export type NuCodeStoreCredentialsRequest = components["schemas"]["NuCodeStoreCredentialsRequest"];
export type NuCodeDevicePollRequest = components["schemas"]["NuCodeDevicePollRequest"];
export type CredentialSummary = components["schemas"]["CredentialResponse"];
export type NuCodeCredentialField = components["schemas"]["NuCodeCredentialFieldResponse"];
export type NuCodeProvider = components["schemas"]["NuCodeProviderResponse"];
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

export interface ResumeSessionResponse {
  instanceId: string;
  session: FleetSession;
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
}

export interface HarnessInfo {
  type: string;
  displayName: string;
  available: boolean;
  userEnabled: boolean;
  reason?: string;
  capabilities: HarnessCapabilities;
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

export interface NuCodeDeviceFlowInitiatedResponse {
  instructions: string;
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
  origin?: SessionOrigin | null;
  tags: readonly string[];
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

export interface NuCodeTestConnectionResponse {
  success: boolean;
  error?: string;
  latencyMs: number;
}

export interface StoreCredentialRequest {
  label: string;
  namespace: string;
  kind: string;
  value: string;
  metadata: string | null;
}

export interface NuCodeDeviceCodeResponse {
  deviceCode: string;
  userCode: string;
  verificationUri: string;
  expiresIn: number;
  interval: number;
}

export interface NuCodeDevicePollResponse {
  status: string;
  interval?: number | null;
  message?: string | null;
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

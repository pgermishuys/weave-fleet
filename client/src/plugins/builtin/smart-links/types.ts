/**
 * A URL detected in a session message, enriched with live status from an external provider.
 * Mirrors the SmartLink domain entity on the backend.
 */
export interface SmartLink {
  id: string
  sessionId: string
  url: string
  providerId: string
  resourceType: string
  resourceId: string
  title: string
  status: string
  statusLabel: string
  metadata: Record<string, unknown>
  isDismissed: boolean
  isTerminal: boolean
  createdAt: string
  updatedAt: string
}

/**
 * The resolved information returned by a provider when it successfully handles a URL.
 */
export interface SmartLinkResolution {
  providerId: string
  resourceType: string
  resourceId: string
  title: string
  status: string
  statusLabel: string
  isTerminal: boolean
  metadata: Record<string, unknown>
}

/**
 * A provider that can detect and resolve URLs into enriched SmartLink metadata.
 * Providers register themselves with the SmartLink provider registry.
 */
export interface SmartLinkProvider {
  /** Unique identifier for this provider (e.g. "github", "linear"). */
  id: string
  /** Returns true if this provider can handle the given URL. */
  canHandle(url: string): boolean
  /** Resolves the URL into enriched metadata. Returns null if resolution fails. */
  resolve(url: string): Promise<SmartLinkResolution | null>
}

/** A single check run from a GitHub CI status check. */
export interface CheckRun {
  id: number
  name: string
  status: string
  conclusion: string | null
  htmlUrl: string
  workflowName: string | null
  startedAt: string | null
  completedAt: string | null
}

/** Aggregate CI status for a pull request. */
export interface CiStatus {
  /** The commit SHA the CI ran against. */
  headSha: string
  /** Aggregate status: "success" | "failure" | "pending" | "neutral" | "none" */
  ciStatus: string
  checkRuns: CheckRun[]
}

/**
 * A CI failure detected by the backend watcher, stored in smart link metadata.
 * Used to surface a "Diagnose" button in the UI for user-initiated analysis.
 */
export interface CiFailure {
  sha: string
  checkRunName: string
  checkRunId: number
  conclusion: string
  htmlUrl: string
  /** Extracted log content (up to 200 lines), or null if unavailable. */
  logContent: string | null
  detectedAt: string
}

/** A single review comment within a thread. */
export interface ReviewComment {
  id: string
  databaseId: number
  body: string
  authorLogin: string
  createdAt: string
  url: string
}

/** A review thread on a pull request. */
export interface ReviewThread {
  threadNodeId: string
  isResolved: boolean
  isOutdated: boolean
  path: string
  line: number | null
  comments: ReviewComment[]
}

/** Summary of review threads for a pull request. */
export interface ReviewThreadSummary {
  unresolvedCount: number
  threads: ReviewThread[]
}

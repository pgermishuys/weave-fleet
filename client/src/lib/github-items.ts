/**
 * Pull requests and issues as the GitHub pages show them: list rows (`GitHubItemSummary`) and a page
 * (`GitHubItemDetail`), from Fleet's GitHub endpoints, which read them from GitHub's GraphQL API.
 */
import { apiFetch } from "@/lib/api-client"
import { createGitHubSessionSourcePreset, type GitHubSessionSourcePreset } from "@/lib/github-session-source"
import { checksFromCounts, checksFromRollup, parseReviewers, reviewDecision, type CheckCounts, type PrFacts, type Reviewer } from "@/lib/pr-state"

export interface GitHubItemLabel {
  name: string
  color: string
}

export interface GitHubItemSummary {
  kind: "pull" | "issue"
  owner: string
  repo: string
  number: number
  title: string
  url: string
  /** open, closed or merged. */
  state: string
  isDraft: boolean
  author: string | null
  authorAvatarUrl: string | null
  createdAt: string
  updatedAt: string
  comments: number
  labels: GitHubItemLabel[]
  headRef: string | null
  baseRef: string | null
  additions: number | null
  deletions: number | null
  /** success, failure, pending or none; null for issues. */
  checks: string | null
  reviewDecision: string | null
  /** MERGEABLE, CONFLICTING or UNKNOWN. */
  mergeable: string | null
  reviewers: Reviewer[]
  assignees: string[]
}

export interface GitHubItemPage {
  items: GitHubItemSummary[]
  totalCount: number
  endCursor: string | null
  hasNextPage: boolean
}

export interface GitHubCheck {
  name: string
  /** success, failure, pending, skipped or neutral. */
  state: "success" | "failure" | "pending" | "skipped" | "neutral"
  url: string | null
  workflowName: string | null
  checkRunId: number | null
  startedAt: string | null
  completedAt: string | null
}

export interface GitHubTimelineEntry {
  kind: "comment" | "review" | "merged" | "closed" | "reopened" | "ready" | "draft" | "referenced"
  author: string | null
  authorAvatarUrl: string | null
  createdAt: string
  body: string | null
  /** A review's verdict, or the referencing item's state. */
  state: string | null
  url: string | null
  reference: { kind: "pull" | "issue"; repository: string; number: number } | null
}

export interface GitHubItemDetail {
  summary: GitHubItemSummary
  body: string | null
  changedFiles: number | null
  mergedAt: string | null
  mergedBy: string | null
  checks: GitHubCheck[]
  unresolvedThreads: number
  timeline: GitHubTimelineEntry[]
}

export interface GitHubRepoCounts {
  fullName: string
  openPullRequests: number
  openIssues: number
}

export interface GitHubWork {
  login: string | null
  reviewRequested: GitHubItemSummary[]
  authored: GitHubItemSummary[]
  assigned: GitHubItemSummary[]
  repos: GitHubRepoCounts[]
}

/** A GitHub request that failed, with the message to show. */
export class GitHubItemsError extends Error {
  constructor(message: string, readonly status: number) {
    super(message)
  }
}

async function getJson<T>(path: string, signal?: AbortSignal): Promise<T> {
  const response = await apiFetch(path, { signal })
  if (!response.ok) {
    const payload = (await response.json().catch(() => ({}))) as { error?: string }
    const message = response.status === 401
      ? "Connect GitHub in Settings to see this."
      : payload.error ?? `GitHub didn't answer (HTTP ${response.status}).`
    throw new GitHubItemsError(message, response.status)
  }
  return normalize(await response.json()) as T
}

// Reviewers arrive as plain objects; give them the typed shape everywhere they appear.
function normalize(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(normalize)
  if (!value || typeof value !== "object") return value
  const entries = Object.entries(value as Record<string, unknown>).map(([key, entry]) =>
    [key, key === "reviewers" ? parseReviewers(entry) : normalize(entry)])
  return Object.fromEntries(entries)
}

const encode = encodeURIComponent

export function searchGitHubItems(q: string, options: { after?: string | null; first?: number; signal?: AbortSignal } = {}): Promise<GitHubItemPage> {
  const params = new URLSearchParams({ q, first: String(options.first ?? 30) })
  if (options.after) params.set("after", options.after)
  return getJson(`/api/integrations/github/search?${params.toString()}`, options.signal)
}

export function fetchGitHubItemsByNumber(owner: string, repo: string, numbers: number[], signal?: AbortSignal): Promise<GitHubItemSummary[]> {
  if (numbers.length === 0) return Promise.resolve([])
  return getJson(`/api/integrations/github/repos/${encode(owner)}/${encode(repo)}/items?numbers=${numbers.join(",")}`, signal)
}

export function fetchGitHubItem(owner: string, repo: string, number: number, signal?: AbortSignal): Promise<GitHubItemDetail> {
  return getJson(`/api/integrations/github/repos/${encode(owner)}/${encode(repo)}/items/${number}`, signal)
}

export function fetchGitHubWork(signal?: AbortSignal): Promise<GitHubWork> {
  return getJson("/api/integrations/github/work", signal)
}

/** `owner/repo#123`, the key smart links use. */
export const itemResourceId = (item: Pick<GitHubItemSummary, "owner" | "repo" | "number">): string =>
  `${item.owner}/${item.repo}#${item.number}`

export function checkCounts(checks: GitHubCheck[]): CheckCounts {
  return {
    passed: checks.filter((c) => c.state === "success" || c.state === "skipped" || c.state === "neutral").length,
    failing: checks.filter((c) => c.state === "failure").length,
    pending: checks.filter((c) => c.state === "pending").length,
  }
}

/** What the pull request state language needs from a list row, or from a page when it has the checks. */
export function itemPrFacts(item: GitHubItemSummary, detail?: Pick<GitHubItemDetail, "checks" | "unresolvedThreads">): PrFacts {
  const counts = detail && detail.checks.length > 0 ? checkCounts(detail.checks) : undefined
  return {
    state: item.state === "merged" ? "merged" : item.state === "closed" ? "closed" : "open",
    draft: item.isDraft,
    checks: counts ? checksFromCounts(counts) : checksFromRollup(item.checks),
    conflict: item.mergeable === "CONFLICTING",
    review: reviewDecision(item.reviewDecision),
    unresolvedThreads: detail?.unresolvedThreads ?? 0,
    checkCounts: counts,
  }
}

/** What "Start session" hands the new-session composer. */
export function itemSessionPreset(item: GitHubItemSummary, body: string | null = null): GitHubSessionSourcePreset {
  const isPull = item.kind === "pull"
  return createGitHubSessionSourcePreset({
    sourceType: isPull ? "github-pull-request" : "github-issue",
    owner: item.owner,
    repo: item.repo,
    number: item.number,
    title: item.title,
    body,
    htmlUrl: item.url,
    repoFullName: `${item.owner}/${item.repo}`,
    suggestedBranch: isPull ? item.headRef : null,
  })
}

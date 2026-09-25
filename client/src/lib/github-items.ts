/**
 * Pull requests and issues as the GitHub pages show them: list rows (`GitHubItemSummary`) and a page
 * (`GitHubItemDetail`), from Fleet's GitHub endpoints, which read them from GitHub's GraphQL API.
 */
import { apiFetch } from "@/lib/api-client"
import { createGitHubSessionSourcePreset, type GitHubSessionSourcePreset } from "@/lib/github-session-source"
import type { IssueFilterState } from "@/plugins/builtin/github/composables/github-types"
import { checksFromCounts, checksFromRollup, parseReviewers, prState, prWords, reviewDecision, type CheckCounts, type PrFacts, type Reviewer } from "@/lib/pr-state"
import { linkDiff, linkLabels, linkNumber, linkReviewers, linkTitle, summarizeChecks, isPullRequest, linkHref, type SmartLink } from "@/lib/smart-links"

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
  avatarUrl: string | null
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

/** A body as GitHub shows it: HTML comments (bots leave notes in them) are hidden there, so they are here. */
export function visibleBody(body: string | null | undefined): string {
  return (body ?? "").replace(/<!--[\s\S]*?-->/g, "").trim()
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

const quote = (value: string) => (/\s/.test(value) ? `"${value}"` : value)

/** The issue filter bar's choices as GitHub search qualifiers, e.g. `is:open label:bug sort:updated-desc`. */
export function issueFilterQuery(filter: IssueFilterState): string {
  const parts: string[] = []
  if (filter.state !== "all") parts.push(`is:${filter.state}`)
  for (const label of filter.labels) parts.push(`label:${quote(label)}`)
  if (filter.author) parts.push(`author:${filter.author}`)
  if (filter.assignee === "none") parts.push("no:assignee")
  else if (filter.assignee && filter.assignee !== "*") parts.push(`assignee:${filter.assignee}`)
  if (filter.milestone) parts.push(`milestone:${quote(filter.milestone)}`)
  if (filter.type) parts.push(`type:${quote(filter.type)}`)
  parts.push(`sort:${filter.sort}-${filter.direction}`)
  if (filter.search.trim()) parts.push(filter.search.trim())
  return parts.join(" ")
}

/**
 * A session's link as a list row, for "In Fleet": the watcher already keeps its state, checks and reviews,
 * so the row needs no request to GitHub.
 */
export function summaryFromLink(link: SmartLink): GitHubItemSummary | null {
  const number = linkNumber(link)
  const { owner, repo } = link.metadata
  if (number === null || typeof owner !== "string" || typeof repo !== "string") return null
  const isPull = isPullRequest(link)
  const checks = summarizeChecks(link)
  const diff = linkDiff(link)
  const meta = (key: string) => (typeof link.metadata[key] === "string" ? (link.metadata[key] as string) : null)
  return {
    kind: isPull ? "pull" : "issue",
    owner,
    repo,
    number,
    title: linkTitle(link),
    url: linkHref(link),
    state: link.status === "draft" ? "open" : link.status || "open",
    isDraft: link.status === "draft",
    author: meta("author"),
    authorAvatarUrl: meta("authorAvatarUrl"),
    createdAt: link.createdAt,
    // When GitHub last saw a change, not when Fleet last checked.
    updatedAt: meta("updatedAt") ?? link.updatedAt,
    comments: 0,
    labels: linkLabels(link),
    headRef: meta("headRef"),
    baseRef: meta("baseRef"),
    additions: diff?.additions ?? null,
    deletions: diff?.deletions ?? null,
    checks: isPull ? (checks.state === "failing" ? "failure" : checks.state === "running" ? "pending" : checks.state === "passed" ? "success" : "none") : null,
    reviewDecision: meta("reviewDecision"),
    mergeable: link.metadata.mergeable === false ? "CONFLICTING" : link.metadata.mergeable === true ? "MERGEABLE" : null,
    reviewers: linkReviewers(link),
    assignees: [],
  }
}

export interface NeedsYouItem {
  item: GitHubItemSummary
  /** Why it's here: "Review requested", "Checks failing", "Changes requested". */
  reason: string
}

/**
 * What needs the user: pull requests waiting on their review, then their own pull requests something blocks
 * (a failing check, a conflict, requested changes).
 */
export function needsYou(work: GitHubWork): NeedsYouItem[] {
  const blocked = work.authored
    .filter((item) => prState(itemPrFacts(item)) === "blocked")
    .map((item) => ({ item, reason: prWords(itemPrFacts(item)) }))
  return [...work.reviewRequested.map((item) => ({ item, reason: "Review requested" })), ...blocked]
}

/** The user's open pull requests that nothing blocks. */
export function yourPullRequests(work: GitHubWork): GitHubItemSummary[] {
  return work.authored.filter((item) => prState(itemPrFacts(item)) !== "blocked")
}

/** The Fleet route for a pull request or issue page. */
export function itemRoute(item: Pick<GitHubItemSummary, "kind" | "owner" | "repo" | "number">): string {
  return `/github/${encodeURIComponent(item.owner)}/${encodeURIComponent(item.repo)}/${item.kind === "pull" ? "pulls" : "issues"}/${item.number}`
}

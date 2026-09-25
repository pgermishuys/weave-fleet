/**
 * One way to say what state a pull request is in, used by every GitHub surface: the sessions list badge, the
 * session header pill, the Context card, the GitHub lists and the pull request page. Each surface turns what it
 * knows into `PrFacts`; the state, colour and words come from here.
 */

/** The colour a pull request shows in. "blocked" is an open pull request something stops. */
export type PrState = "open" | "draft" | "blocked" | "merged" | "closed"

export type ChecksState = "passing" | "failing" | "pending" | "none"

export type ReviewDecision = "APPROVED" | "CHANGES_REQUESTED" | "REVIEW_REQUIRED"

/** What stops an open pull request, in the order the words list them. */
export type BlockReason = "checks" | "conflict" | "changes" | "threads"

export interface PrFacts {
  /** GitHub's state; a draft is `open` with `draft` set. */
  state: "open" | "closed" | "merged"
  draft: boolean
  checks: ChecksState
  conflict: boolean
  review: ReviewDecision | null
  unresolvedThreads: number
  /** Check counts, when known, for "1 failing" and "3 of 5 checks done". */
  checkCounts?: CheckCounts
}

export interface CheckCounts {
  passed: number
  failing: number
  pending: number
}

export function blockReasons(facts: PrFacts): BlockReason[] {
  if (facts.state !== "open") return []
  const reasons: BlockReason[] = []
  if (facts.checks === "failing") reasons.push("checks")
  if (facts.conflict) reasons.push("conflict")
  if (facts.review === "CHANGES_REQUESTED") reasons.push("changes")
  if (facts.unresolvedThreads > 0) reasons.push("threads")
  return reasons
}

export function prState(facts: PrFacts): PrState {
  if (facts.state === "merged") return "merged"
  if (facts.state === "closed") return "closed"
  // A draft isn't asking for anything yet, so it stays grey whatever its checks say.
  if (facts.draft) return "draft"
  return blockReasons(facts).length > 0 ? "blocked" : "open"
}

const plural = (count: number, word: string) => `${count} ${word}${count === 1 ? "" : "s"}`

function reasonWords(reason: BlockReason, facts: PrFacts): string {
  switch (reason) {
    case "checks": {
      const failing = facts.checkCounts?.failing ?? 0
      return failing > 0 ? `${failing} failing` : "Checks failing"
    }
    case "conflict": return "Conflicts"
    case "changes": return "Changes requested"
    case "threads": return plural(facts.unresolvedThreads, "thread")
  }
}

/**
 * A few words for what the pull request is waiting on: "1 failing · 2 threads", "3 of 5 checks done",
 * "Ready to merge". The pill and the badge's tooltip show them.
 */
export function prWords(facts: PrFacts): string {
  switch (prState(facts)) {
    case "merged": return "Merged"
    case "closed": return "Closed"
    case "draft": return facts.checks === "failing" ? "Draft · checks failing" : "Draft"
    case "blocked": {
      const words = blockReasons(facts).map((reason) => reasonWords(reason, facts))
      // "1 failing" alone doesn't say what failed.
      if (words.length === 1 && words[0].endsWith(" failing")) return "Checks failing"
      // Two reasons fit a pill; the rest are a count, and the popover lists them.
      return words.length > 2 ? `${words.slice(0, 2).join(" · ")} +${words.length - 2}` : words.join(" · ")
    }
    case "open": break
  }

  if (facts.checks === "pending") {
    const counts = facts.checkCounts
    const total = counts ? counts.passed + counts.failing + counts.pending : 0
    return counts && total > 0 ? `${counts.passed} of ${total} checks done` : "Checks running"
  }
  if (facts.review === "APPROVED") return facts.checks === "none" ? "Approved" : "Ready to merge"
  if (facts.review === "REVIEW_REQUIRED") return "Waiting for review"
  return facts.checks === "passing" ? "Checks passed" : "Open"
}

/** A label for the state alone: "Open", "Blocked", "Draft", "Merged", "Closed". */
export function prStateLabel(state: PrState): string {
  return state[0].toUpperCase() + state.slice(1)
}

/** GitHub's own words for the checks, so they read the same here as on github.com. */
export function checksHeadline(checks: ChecksState): string {
  switch (checks) {
    case "failing": return "Some checks were not successful"
    case "pending": return "Some checks haven't completed yet"
    case "passing": return "All checks have passed"
    case "none": return "No checks"
  }
}

/** "3 passed · 1 failing · 1 running". */
export function checkCountWords(counts: CheckCounts): string {
  return [
    counts.passed ? `${counts.passed} passed` : "",
    counts.failing ? `${counts.failing} failing` : "",
    counts.pending ? `${counts.pending} running` : "",
  ].filter(Boolean).join(" · ")
}

/** GitHub's rollup ("success", "failure", "pending", "none") in Fleet's words. */
export function checksFromRollup(rollup: string | null | undefined): ChecksState {
  switch (rollup) {
    case "success": return "passing"
    case "failure": return "failing"
    case "pending": return "pending"
    default: return "none"
  }
}

export function checksFromCounts(counts: CheckCounts): ChecksState {
  if (counts.failing > 0) return "failing"
  if (counts.pending > 0) return "pending"
  return counts.passed > 0 ? "passing" : "none"
}

export function reviewDecision(value: unknown): ReviewDecision | null {
  return value === "APPROVED" || value === "CHANGES_REQUESTED" || value === "REVIEW_REQUIRED" ? value : null
}

/** A reviewer's latest verdict, or REQUESTED while they haven't answered. */
export type ReviewerState = "APPROVED" | "CHANGES_REQUESTED" | "COMMENTED" | "DISMISSED" | "REQUESTED"

export interface Reviewer {
  login: string
  avatarUrl: string | null
  state: ReviewerState
}

export function reviewerWords(state: ReviewerState): string {
  switch (state) {
    case "APPROVED": return "Approved"
    case "CHANGES_REQUESTED": return "Changes requested"
    case "COMMENTED": return "Commented"
    case "DISMISSED": return "Dismissed"
    case "REQUESTED": return "Review requested"
  }
}

export function parseReviewers(value: unknown): Reviewer[] {
  if (!Array.isArray(value)) return []
  return value.flatMap((entry): Reviewer[] => {
    if (!entry || typeof entry !== "object") return []
    const { login, avatarUrl, state } = entry as Record<string, unknown>
    if (typeof login !== "string") return []
    const known: ReviewerState[] = ["APPROVED", "CHANGES_REQUESTED", "COMMENTED", "DISMISSED", "REQUESTED"]
    return [{
      login,
      avatarUrl: typeof avatarUrl === "string" && avatarUrl ? avatarUrl : null,
      state: known.find((s) => s === state) ?? "COMMENTED",
    }]
  })
}

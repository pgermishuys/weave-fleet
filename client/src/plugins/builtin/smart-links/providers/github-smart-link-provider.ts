import { apiFetch } from '@/lib/api-client'
import type { SmartLinkProvider, SmartLinkResolution, CiStatus, CheckRun, ReviewThreadSummary } from '../types'

const PR_PATTERN = /^https?:\/\/github\.com\/([^/]+)\/([^/]+)\/pull\/(\d+)/
const ISSUE_PATTERN = /^https?:\/\/github\.com\/([^/]+)\/([^/]+)\/issues\/(\d+)/

interface GitHubLabel {
  name: string
  color: string
}

interface GitHubPRResponse {
  number: number
  title: string
  state: 'open' | 'closed'
  merged: boolean
  draft: boolean
  html_url: string
  labels: GitHubLabel[]
}

interface GitHubIssueResponse {
  number: number
  title: string
  state: 'open' | 'closed'
  html_url: string
  labels: GitHubLabel[]
}

interface GitHubCiStatusResponse {
  headSha: string
  ciStatus: string
  checkRuns: Array<{
    id: number
    name: string
    status: string
    conclusion: string | null
    htmlUrl: string
    workflowName: string | null
    startedAt: string | null
    completedAt: string | null
  }>
}

function prStatus(pr: GitHubPRResponse): { status: string; statusLabel: string; isTerminal: boolean } {
  if (pr.merged) {
    return { status: 'merged', statusLabel: 'Merged', isTerminal: true }
  }
  if (pr.state === 'closed') {
    return { status: 'closed', statusLabel: 'Closed', isTerminal: true }
  }
  if (pr.draft) {
    return { status: 'draft', statusLabel: 'Draft', isTerminal: false }
  }
  return { status: 'open', statusLabel: 'Open', isTerminal: false }
}

function issueStatus(issue: GitHubIssueResponse): { status: string; statusLabel: string; isTerminal: boolean } {
  if (issue.state === 'closed') {
    return { status: 'closed', statusLabel: 'Closed', isTerminal: true }
  }
  return { status: 'open', statusLabel: 'Open', isTerminal: false }
}

async function fetchCiStatus(owner: string, repo: string, number: string): Promise<CiStatus | null> {
  try {
    const response = await apiFetch(
      `/api/integrations/github/repos/${owner}/${repo}/pulls/${number}/status`,
    )
    if (!response.ok) return null
    const data = (await response.json()) as GitHubCiStatusResponse
    const checkRuns: CheckRun[] = data.checkRuns.map((cr) => ({
      id: cr.id,
      name: cr.name,
      status: cr.status,
      conclusion: cr.conclusion,
      htmlUrl: cr.htmlUrl,
      workflowName: cr.workflowName,
      startedAt: cr.startedAt,
      completedAt: cr.completedAt,
    }))
    return {
      headSha: data.headSha,
      ciStatus: data.ciStatus,
      checkRuns,
    }
  } catch {
    return null
  }
}

async function fetchReviewThreads(owner: string, repo: string, number: string): Promise<ReviewThreadSummary | null> {
  try {
    const response = await apiFetch(
      `/api/integrations/github/repos/${owner}/${repo}/pulls/${number}/review-threads`,
    )
    if (!response.ok) return null
    return (await response.json()) as ReviewThreadSummary
  } catch {
    return null
  }
}

export const githubSmartLinkProvider: SmartLinkProvider = {
  id: 'github',

  canHandle(url: string): boolean {
    return PR_PATTERN.test(url) || ISSUE_PATTERN.test(url)
  },

  async resolve(url: string): Promise<SmartLinkResolution | null> {
    const prMatch = PR_PATTERN.exec(url)
    if (prMatch) {
      const [, owner, repo, number] = prMatch
      try {
        const response = await apiFetch(
          `/api/integrations/github/repos/${owner}/${repo}/pulls/${number}`,
        )
        if (!response.ok) return null
        const pr = (await response.json()) as GitHubPRResponse
        const { status, statusLabel, isTerminal } = prStatus(pr)

        // Fetch CI status for non-terminal PRs
        const ci = isTerminal ? null : await fetchCiStatus(owner, repo, number)

        // Fetch review threads for non-terminal PRs
        const reviewThreads = isTerminal ? null : await fetchReviewThreads(owner, repo, number)

        return {
          providerId: 'github',
          resourceType: 'pull_request',
          resourceId: `${owner}/${repo}#${number}`,
          title: `${owner}/${repo} #${number}: ${pr.title}`,
          status,
          statusLabel,
          isTerminal,
          metadata: {
            owner,
            repo,
            number: pr.number,
            draft: pr.draft,
            labels: pr.labels ?? [],
            ...(ci ? { ci } : {}),
            ...(reviewThreads ? { reviewThreads } : {}),
          },
        }
      } catch {
        return null
      }
    }

    const issueMatch = ISSUE_PATTERN.exec(url)
    if (issueMatch) {
      const [, owner, repo, number] = issueMatch
      try {
        const response = await apiFetch(
          `/api/integrations/github/repos/${owner}/${repo}/issues/${number}`,
        )
        if (!response.ok) return null
        const issue = (await response.json()) as GitHubIssueResponse
        const { status, statusLabel, isTerminal } = issueStatus(issue)
        return {
          providerId: 'github',
          resourceType: 'issue',
          resourceId: `${owner}/${repo}#${number}`,
          title: `${owner}/${repo} #${number}: ${issue.title}`,
          status,
          statusLabel,
          isTerminal,
          metadata: { owner, repo, number: issue.number, labels: issue.labels ?? [] },
        }
      } catch {
        return null
      }
    }

    return null
  },
}

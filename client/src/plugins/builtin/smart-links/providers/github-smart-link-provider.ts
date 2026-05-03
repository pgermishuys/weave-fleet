import { apiFetch } from '@/lib/api-client'
import type { SmartLinkProvider, SmartLinkResolution } from '../types'

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
        return {
          providerId: 'github',
          resourceType: 'pull_request',
          resourceId: `${owner}/${repo}#${number}`,
          title: `${owner}/${repo} #${number}: ${pr.title}`,
          status,
          statusLabel,
          isTerminal,
          metadata: { owner, repo, number: pr.number, draft: pr.draft, labels: pr.labels ?? [] },
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

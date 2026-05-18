import type { ReviewCommentQueueItem } from '@/stores/review-comment-queue'

const PR_REVIEW_HEADER_RE = /\[PR Review Comments — ([^/]+)\/([^\s]+) PR #(\d+)\]/

interface ParsedComment {
  threadNodeId: string
  commentId: number
  path: string
  line: number | null
  authorLogin: string
  originalBody: string
  commentUrl: string
}

/**
 * Parses the structured prompt injected by ReviewCommentWatcherService.
 * Returns null if the text is not a review comment notification.
 */
export function parseReviewCommentPrompt(
  text: string,
): { owner: string; repo: string; prNumber: number; comments: ParsedComment[] } | null {
  const headerMatch = PR_REVIEW_HEADER_RE.exec(text)
  if (!headerMatch) return null

  const [, owner, repo, prNumberStr] = headerMatch
  const prNumber = parseInt(prNumberStr, 10)

  const comments: ParsedComment[] = []

  // Each comment block ends with "Thread ID: {nodeId} | Comment ID: {dbId}"
  const THREAD_MARKER_RE = /Thread ID: ([^\s|]+) \| Comment ID: (\d+)/g
  let match: RegExpExecArray | null

  while ((match = THREAD_MARKER_RE.exec(text)) !== null) {
    const threadNodeId = match[1]
    const commentId = parseInt(match[2], 10)

    // Walk backward from this marker to find the ### header with file:line
    const blockText = text.slice(0, match.index)
    const lastHeaderIdx = blockText.lastIndexOf('### ')
    if (lastHeaderIdx === -1) continue

    const headerLine = blockText.slice(lastHeaderIdx + 4, blockText.indexOf('\n', lastHeaderIdx))
    const colonIdx = headerLine.lastIndexOf(':')
    let path: string
    let line: number | null = null
    if (colonIdx > 0) {
      const possibleLine = parseInt(headerLine.slice(colonIdx + 1), 10)
      if (!isNaN(possibleLine)) {
        path = headerLine.slice(0, colonIdx)
        line = possibleLine
      } else {
        path = headerLine
      }
    } else {
      path = headerLine
    }

    // Extract author login from **@{author}** pattern
    const authorMatch = /\*\*@([^*]+)\*\*/.exec(blockText.slice(lastHeaderIdx))
    const authorLogin = authorMatch ? authorMatch[1].trim() : ''

    // Extract original body from blockquote lines
    const bodyLines: string[] = []
    for (const bodyLine of blockText.slice(lastHeaderIdx).split('\n')) {
      if (bodyLine.startsWith('> ')) {
        bodyLines.push(bodyLine.slice(2))
      }
    }
    const originalBody = bodyLines.join('\n').trim()

    // Extract URL from "Link: {url}" line
    const linkMatch = /^Link: (.+)$/m.exec(blockText.slice(lastHeaderIdx))
    const commentUrl = linkMatch ? linkMatch[1].trim() : ''

    comments.push({ threadNodeId, commentId, path, line, authorLogin, originalBody, commentUrl })
  }

  if (comments.length === 0) return null
  return { owner, repo, prNumber, comments }
}

/**
 * Builds queue items from a parsed prompt. Optionally pre-fills proposed replies
 * by doing a best-effort extraction from the following agent response text.
 */
export function buildQueueItems(
  sessionId: string,
  parsed: { owner: string; repo: string; prNumber: number; comments: ParsedComment[] },
  agentResponseText?: string,
): ReviewCommentQueueItem[] {
  return parsed.comments.map((c) => ({
    id: `${sessionId}:${c.commentId}`,
    sessionId,
    threadNodeId: c.threadNodeId,
    commentId: c.commentId,
    path: c.path,
    line: c.line,
    authorLogin: c.authorLogin,
    originalBody: c.originalBody,
    commentUrl: c.commentUrl,
    prOwner: parsed.owner,
    prRepo: parsed.repo,
    prNumber: parsed.prNumber,
    proposedReply: agentResponseText
      ? extractProposedReply(agentResponseText, c.path, c.line, c.threadNodeId)
      : '',
    status: 'pending',
  }))
}

/**
 * Best-effort extraction of proposed reply text from an agent response for a specific
 * thread. Searches for the file:line or Thread ID reference and extracts the surrounding paragraph.
 */
function extractProposedReply(
  agentText: string,
  path: string,
  line: number | null,
  threadNodeId: string,
): string {
  // Try Thread ID match first (most specific)
  const threadMatch = new RegExp(`Thread ID:\\s*${escapeRegex(threadNodeId)}`, 'i').exec(agentText)
  if (threadMatch) {
    return extractParagraphAfter(agentText, threadMatch.index + threadMatch[0].length)
  }

  // Try file:line match
  const location = line !== null ? `${path}:${line}` : path
  const locationMatch = new RegExp(escapeRegex(location), 'i').exec(agentText)
  if (locationMatch) {
    return extractParagraphAfter(agentText, locationMatch.index + locationMatch[0].length)
  }

  // Try just the filename
  const filename = path.split('/').pop() ?? path
  const filenameMatch = new RegExp(escapeRegex(filename), 'i').exec(agentText)
  if (filenameMatch) {
    return extractParagraphAfter(agentText, filenameMatch.index + filenameMatch[0].length)
  }

  return ''
}

function extractParagraphAfter(text: string, fromIndex: number): string {
  const remainder = text.slice(fromIndex)
  // Find next non-empty paragraph — skip blank lines, headers, and code fences
  const paragraphs = remainder
    .split(/\n{2,}/)
    .map((p) => p.trim())
    .filter((p) => p.length > 0 && !p.startsWith('#') && !p.startsWith('```'))
  return paragraphs[0] ?? ''
}

function escapeRegex(str: string): string {
  return str.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
}

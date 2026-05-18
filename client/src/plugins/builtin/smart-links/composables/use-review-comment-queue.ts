import { watch, type Ref } from 'vue'
import type { AccumulatedMessage } from '@/lib/api-types'
import { apiFetch } from '@/lib/api-client'
import { useReviewCommentQueueStore, type ReviewCommentQueueItem } from '@/stores/review-comment-queue'
import { parseReviewCommentPrompt, buildQueueItems } from '../utils/parse-review-proposals'

const PR_REVIEW_HEADER = '[PR Review Comments —'

export interface UseReviewCommentQueueOptions {
  sessionId: Ref<string | null>
  messages: Ref<readonly AccumulatedMessage[]>
  onNewItems?: (count: number, prRef: string) => void
}

function extractText(msg: AccumulatedMessage): string {
  return msg.parts
    .filter((p): p is Extract<typeof p, { type: 'text' }> => p.type === 'text')
    .map((p) => p.text)
    .join('\n')
}

function isReviewCommentPrompt(msg: AccumulatedMessage): boolean {
  return msg.role === 'user' && extractText(msg).includes(PR_REVIEW_HEADER)
}

export function useReviewCommentQueue(options: UseReviewCommentQueueOptions): {
  approve: (item: ReviewCommentQueueItem) => Promise<void>
  skip: (item: ReviewCommentQueueItem) => void
  updateReply: (item: ReviewCommentQueueItem, reply: string) => void
  approveAll: (sessionId: string) => Promise<void>
} {
  const { sessionId, messages, onNewItems } = options
  const store = useReviewCommentQueueStore()

  // Track which prompt message IDs we've already processed
  const processedPromptIds = new Set<string>()

  function processMessages(msgs: readonly AccumulatedMessage[], sid: string): void {
    for (let i = 0; i < msgs.length; i++) {
      const msg = msgs[i]
      if (!isReviewCommentPrompt(msg)) continue
      if (processedPromptIds.has(msg.messageId)) continue

      const promptText = extractText(msg)
      const parsed = parseReviewCommentPrompt(promptText)
      if (!parsed) continue

      // Look for the next assistant message as the agent response
      const nextAssistant = msgs.slice(i + 1).find((m) => m.role === 'assistant')
      const agentText = nextAssistant ? extractText(nextAssistant) : undefined

      // Only mark as processed once we have an agent response (or there are no more messages)
      const isLastMessage = i === msgs.length - 1
      if (!nextAssistant && !isLastMessage) continue

      processedPromptIds.add(msg.messageId)

      const newItems = buildQueueItems(sid, parsed, agentText)
      if (newItems.length > 0) {
        const notificationText = `ℹ️ ${newItems.length} new review comment${newItems.length > 1 ? 's' : ''} on ${parsed.owner}/${parsed.repo} #${parsed.prNumber}`
        store.addItems(newItems, notificationText)
        onNewItems?.(newItems.length, `${parsed.owner}/${parsed.repo} #${parsed.prNumber}`)
      }
    }
  }

  watch(
    [sessionId, messages] as const,
    ([sid, msgs]) => {
      if (!sid) return
      processMessages(msgs, sid)
    },
    { immediate: true, deep: false },
  )

  async function approve(item: ReviewCommentQueueItem): Promise<void> {
    if (!item.proposedReply.trim()) return

    store.updateStatus(item.id, 'replying')

    try {
      // 1. Post the reply
      const replyResponse = await apiFetch(
        `/api/integrations/github/repos/${encodeURIComponent(item.prOwner)}/${encodeURIComponent(item.prRepo)}/pulls/${item.prNumber}/comments/${item.commentId}/replies`,
        {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ body: item.proposedReply }),
        },
      )

      if (!replyResponse.ok) {
        const errorText = await replyResponse.text().catch(() => 'Unknown error')
        store.updateStatus(item.id, 'failed', `Reply failed: ${errorText}`)
        return
      }

      // 2. Resolve the thread only after successful reply
      const resolveResponse = await apiFetch(
        `/api/integrations/github/repos/${encodeURIComponent(item.prOwner)}/${encodeURIComponent(item.prRepo)}/pulls/${item.prNumber}/threads/${encodeURIComponent(item.threadNodeId)}/resolve`,
        { method: 'POST' },
      )

      if (!resolveResponse.ok) {
        // Reply succeeded but resolve failed — still mark resolved with a note
        store.updateStatus(item.id, 'resolved', 'Thread resolve failed; reply was posted.')
        return
      }

      store.updateStatus(item.id, 'resolved')
    } catch (err) {
      const msg = err instanceof Error ? err.message : 'Unknown error'
      store.updateStatus(item.id, 'failed', msg)
    }
  }

  function skip(item: ReviewCommentQueueItem): void {
    store.updateStatus(item.id, 'skipped')
  }

  function updateReply(item: ReviewCommentQueueItem, reply: string): void {
    store.updateProposedReply(item.id, reply)
  }

  async function approveAll(sid: string): Promise<void> {
    const pending = store.getItemsForSession(sid).filter((i) => i.status === 'pending')
    for (const item of pending) {
      await approve(item)
    }
  }

  return { approve, skip, updateReply, approveAll }
}

import { defineStore } from 'pinia'
import { ref, computed } from 'vue'

export type ReviewCommentQueueStatus = 'pending' | 'replying' | 'resolved' | 'failed' | 'skipped'

export interface ReviewCommentQueueItem {
  /** Unique key: sessionId + commentId */
  id: string
  sessionId: string
  threadNodeId: string
  commentId: number
  path: string
  line: number | null
  authorLogin: string
  originalBody: string
  commentUrl: string
  prOwner: string
  prRepo: string
  prNumber: number
  proposedReply: string
  status: ReviewCommentQueueStatus
  error?: string
}

export const useReviewCommentQueueStore = defineStore('review-comment-queue', () => {
  const items = ref<ReviewCommentQueueItem[]>([])

  /** Transient notification shown when new items are queued — cleared by consumer */
  const latestNotification = ref<string | null>(null)

  function addItems(newItems: ReviewCommentQueueItem[], notificationText?: string): void {
    let addedCount = 0
    for (const item of newItems) {
      const exists = items.value.some((i) => i.id === item.id)
      if (!exists) {
        items.value.push(item)
        addedCount++
      }
    }
    if (notificationText && addedCount > 0) {
      latestNotification.value = notificationText
    }
  }

  function clearNotification(): void {
    latestNotification.value = null
  }

  function updateStatus(id: string, status: ReviewCommentQueueStatus, error?: string): void {
    const item = items.value.find((i) => i.id === id)
    if (item) {
      item.status = status
      item.error = error
    }
  }

  function updateProposedReply(id: string, reply: string): void {
    const item = items.value.find((i) => i.id === id)
    if (item) {
      item.proposedReply = reply
    }
  }

  function getItemsForSession(sessionId: string): ReviewCommentQueueItem[] {
    return items.value.filter((i) => i.sessionId === sessionId)
  }

  function getPendingCountForSession(sessionId: string): number {
    return items.value.filter((i) => i.sessionId === sessionId && i.status === 'pending').length
  }

  const totalPendingCount = computed(() => items.value.filter((i) => i.status === 'pending').length)

  function clearSession(sessionId: string): void {
    items.value = items.value.filter((i) => i.sessionId !== sessionId)
  }

  return {
    items,
    latestNotification,
    addItems,
    clearNotification,
    updateStatus,
    updateProposedReply,
    getItemsForSession,
    getPendingCountForSession,
    totalPendingCount,
    clearSession,
  }
})

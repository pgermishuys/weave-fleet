import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { useReviewCommentQueueStore } from '@/stores/review-comment-queue'
import type { ReviewCommentQueueItem } from '@/stores/review-comment-queue'

vi.mock('@/lib/api-client', () => ({
  apiFetch: vi.fn(),
}))

import { apiFetch } from '@/lib/api-client'

const mockApiFetch = vi.mocked(apiFetch)

function makeItem(overrides: Partial<ReviewCommentQueueItem> = {}): ReviewCommentQueueItem {
  return {
    id: 'session-1:111',
    sessionId: 'session-1',
    threadNodeId: 'PRRT_abc',
    commentId: 111,
    path: 'src/Foo.cs',
    line: 10,
    authorLogin: 'alice',
    originalBody: 'Please fix this.',
    commentUrl: 'https://github.com/owner/repo/pull/1#discussion_r111',
    prOwner: 'owner',
    prRepo: 'repo',
    prNumber: 1,
    proposedReply: 'Fixed in latest commit.',
    status: 'pending',
    ...overrides,
  }
}

describe('useReviewCommentQueueStore', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    vi.resetAllMocks()
  })

  it('adds new items', () => {
    const store = useReviewCommentQueueStore()
    store.addItems([makeItem()])
    expect(store.items).toHaveLength(1)
  })

  it('deduplicates items by id', () => {
    const store = useReviewCommentQueueStore()
    store.addItems([makeItem()])
    store.addItems([makeItem()])
    expect(store.items).toHaveLength(1)
  })

  it('transitions status from pending to replying to resolved', () => {
    const store = useReviewCommentQueueStore()
    store.addItems([makeItem()])
    store.updateStatus('session-1:111', 'replying')
    expect(store.items[0].status).toBe('replying')
    store.updateStatus('session-1:111', 'resolved')
    expect(store.items[0].status).toBe('resolved')
  })

  it('transitions status to failed with error message', () => {
    const store = useReviewCommentQueueStore()
    store.addItems([makeItem()])
    store.updateStatus('session-1:111', 'failed', 'Network error')
    expect(store.items[0].status).toBe('failed')
    expect(store.items[0].error).toBe('Network error')
  })

  it('transitions status to skipped', () => {
    const store = useReviewCommentQueueStore()
    store.addItems([makeItem()])
    store.updateStatus('session-1:111', 'skipped')
    expect(store.items[0].status).toBe('skipped')
  })

  it('updates proposed reply', () => {
    const store = useReviewCommentQueueStore()
    store.addItems([makeItem()])
    store.updateProposedReply('session-1:111', 'New reply text')
    expect(store.items[0].proposedReply).toBe('New reply text')
  })

  it('reports pending count per session', () => {
    const store = useReviewCommentQueueStore()
    store.addItems([makeItem(), makeItem({ id: 'session-1:222', commentId: 222 })])
    store.updateStatus('session-1:222', 'resolved')
    expect(store.getPendingCountForSession('session-1')).toBe(1)
    expect(store.totalPendingCount).toBe(1)
  })

  it('sets latestNotification when addItems called with notification text', () => {
    const store = useReviewCommentQueueStore()
    store.addItems([makeItem()], 'ℹ️ 1 new review comment on owner/repo #1')
    expect(store.latestNotification).toBe('ℹ️ 1 new review comment on owner/repo #1')
  })

  it('clears notification', () => {
    const store = useReviewCommentQueueStore()
    store.addItems([makeItem()], 'some notification')
    store.clearNotification()
    expect(store.latestNotification).toBeNull()
  })

  it('clears all items for a session', () => {
    const store = useReviewCommentQueueStore()
    store.addItems([makeItem(), makeItem({ id: 'session-2:999', sessionId: 'session-2', commentId: 999 })])
    store.clearSession('session-1')
    expect(store.items).toHaveLength(1)
    expect(store.items[0].sessionId).toBe('session-2')
  })
})

describe('useReviewCommentQueue composable actions', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    vi.resetAllMocks()
  })

  it('approve: posts reply then resolves thread on success', async () => {
    mockApiFetch.mockResolvedValue({ ok: true } as Response)

    const { useReviewCommentQueue } = await import('../composables/use-review-comment-queue')
    const { ref, computed } = await import('vue')

    const store = useReviewCommentQueueStore()
    store.addItems([makeItem()])

    const { approve } = useReviewCommentQueue({
      sessionId: computed(() => 'session-1'),
      messages: ref([]),
    })

    await approve(store.items[0])

    expect(mockApiFetch).toHaveBeenCalledTimes(2)
    expect(mockApiFetch).toHaveBeenCalledWith(
      '/api/integrations/github/repos/owner/repo/pulls/1/comments/111/replies',
      expect.objectContaining({ method: 'POST' }),
    )
    expect(mockApiFetch).toHaveBeenCalledWith(
      '/api/integrations/github/repos/owner/repo/pulls/1/threads/PRRT_abc/resolve',
      expect.objectContaining({ method: 'POST' }),
    )
    expect(store.items[0].status).toBe('resolved')
  })

  it('approve: sets failed status when reply fails', async () => {
    mockApiFetch.mockResolvedValue({ ok: false, text: () => Promise.resolve('Bad request') } as Response)

    const { useReviewCommentQueue } = await import('../composables/use-review-comment-queue')
    const { ref, computed } = await import('vue')

    const store = useReviewCommentQueueStore()
    store.addItems([makeItem()])

    const { approve } = useReviewCommentQueue({
      sessionId: computed(() => 'session-1'),
      messages: ref([]),
    })

    await approve(store.items[0])

    expect(store.items[0].status).toBe('failed')
    expect(mockApiFetch).toHaveBeenCalledTimes(1) // only reply endpoint, not resolve
  })

  it('approve: does nothing when proposedReply is empty', async () => {
    const { useReviewCommentQueue } = await import('../composables/use-review-comment-queue')
    const { ref, computed } = await import('vue')

    const store = useReviewCommentQueueStore()
    store.addItems([makeItem({ proposedReply: '  ' })])

    const { approve } = useReviewCommentQueue({
      sessionId: computed(() => 'session-1'),
      messages: ref([]),
    })

    await approve(store.items[0])

    expect(mockApiFetch).not.toHaveBeenCalled()
    expect(store.items[0].status).toBe('pending')
  })

  it('skip: transitions item to skipped', async () => {
    const { useReviewCommentQueue } = await import('../composables/use-review-comment-queue')
    const { ref, computed } = await import('vue')

    const store = useReviewCommentQueueStore()
    store.addItems([makeItem()])

    const { skip } = useReviewCommentQueue({
      sessionId: computed(() => 'session-1'),
      messages: ref([]),
    })

    skip(store.items[0])
    expect(store.items[0].status).toBe('skipped')
  })

  it('updateReply: updates the proposed reply text', async () => {
    const { useReviewCommentQueue } = await import('../composables/use-review-comment-queue')
    const { ref, computed } = await import('vue')

    const store = useReviewCommentQueueStore()
    store.addItems([makeItem()])

    const { updateReply } = useReviewCommentQueue({
      sessionId: computed(() => 'session-1'),
      messages: ref([]),
    })

    updateReply(store.items[0], 'Edited reply')
    expect(store.items[0].proposedReply).toBe('Edited reply')
  })
})

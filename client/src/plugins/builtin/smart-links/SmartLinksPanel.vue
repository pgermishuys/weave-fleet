<script setup lang="ts">
import { computed, watch, ref } from 'vue'
import { Link } from 'lucide-vue-next'
import { useSessionsStore } from '@/stores/sessions'
import { useSmartLinksStore } from '@/stores/smart-links'
import { useReviewCommentQueueStore } from '@/stores/review-comment-queue'
import { apiFetch } from '@/lib/api-client'
import SmartLinkItem from './SmartLinkItem.vue'
import ReviewCommentQueue from './ReviewCommentQueue.vue'
import { useReviewCommentQueue } from './composables/use-review-comment-queue'
import type { AccumulatedMessage } from '@/lib/api-types'
import type { ReviewCommentQueueItem } from '@/stores/review-comment-queue'

const sessionsStore = useSessionsStore()
const smartLinksStore = useSmartLinksStore()
const queueStore = useReviewCommentQueueStore()

const sessionId = computed(() => sessionsStore.activeSessionId)
const activeLinks = computed(() =>
  sessionId.value ? smartLinksStore.getActiveLinks(sessionId.value) : [],
)
const queueItems = computed(() =>
  sessionId.value ? queueStore.getItemsForSession(sessionId.value) : [],
)

// Notification banner
const notificationText = ref<string | null>(null)
let notificationTimer: ReturnType<typeof setTimeout> | undefined
watch(
  () => queueStore.latestNotification,
  (text) => {
    if (!text) return
    notificationText.value = text
    queueStore.clearNotification()
    clearTimeout(notificationTimer)
    notificationTimer = setTimeout(() => {
      notificationText.value = null
    }, 6000)
  },
)

// Provide empty messages ref so the composable is a no-op here (already wired in ActivityStream)
const emptyMessages = computed<readonly AccumulatedMessage[]>(() => [])
const { approve, skip, updateReply, approveAll } = useReviewCommentQueue({
  sessionId,
  messages: emptyMessages,
})

async function handleDismiss(linkId: string): Promise<void> {
  const sid = sessionId.value
  if (!sid) return
  try {
    const response = await apiFetch(`/api/sessions/${sid}/smart-links/${linkId}/dismiss`, {
      method: 'PATCH',
    })
    if (response.ok) {
      smartLinksStore.dismissLink(sid, linkId)
    }
  } catch {
    // silently ignore
  }
}

function handleApprove(item: ReviewCommentQueueItem): void {
  void approve(item)
}

function handleApproveAll(): void {
  const sid = sessionId.value
  if (sid) void approveAll(sid)
}
</script>

<template>
  <section
    class="smart-links-panel"
    aria-label="Smart links panel"
  >
    <header class="plugin-header">
      <Link
        class="plugin-header-icon"
        :size="16"
        aria-hidden="true"
      />
      <h2 class="plugin-header-title">
        Smart Links
      </h2>
    </header>

    <!-- Notification banner -->
    <div
      v-if="notificationText"
      class="smart-links-notification"
      role="status"
      aria-live="polite"
    >
      {{ notificationText }}
    </div>

    <div
      v-if="!sessionId"
      class="plugin-message-state plugin-message-state--empty"
    >
      <p class="plugin-message-copy">
        Open a session to see detected smart links.
      </p>
    </div>

    <div
      v-else-if="activeLinks.length === 0"
      class="plugin-message-state plugin-message-state--empty"
    >
      <p class="plugin-message-copy">
        No smart links detected yet. Links from GitHub pull requests and issues in messages will appear here.
      </p>
    </div>

    <div
      v-else
      class="plugin-list"
    >
      <SmartLinkItem
        v-for="link in activeLinks"
        :key="link.id"
        :link="link"
        :session-id="sessionId"
        @dismiss="handleDismiss"
      />
    </div>

    <!-- Review comment queue panel -->
    <ReviewCommentQueue
      v-if="sessionId && queueItems.length > 0"
      :items="queueItems"
      @approve="handleApprove"
      @skip="skip"
      @update-reply="(item, reply) => updateReply(item, reply)"
      @approve-all="handleApproveAll"
    />
  </section>
</template>

<style scoped>
.smart-links-panel {
  display: flex;
  flex: 1;
  min-height: 0;
  flex-direction: column;
  background: var(--panel-bg);
}

.plugin-header {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 14px 16px 10px;
}

.plugin-header-icon {
  color: var(--text);
}

.plugin-header-title {
  margin: 0;
  font-size: 16px;
  font-weight: 700;
}

.plugin-message-state {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 10px;
  padding: 14px 12px;
  font-size: 12px;
  color: var(--muted);
}

.plugin-message-state--empty {
  color: var(--muted);
}

.plugin-message-copy {
  margin: 0;
}

.plugin-list {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
}

.smart-links-notification {
  background: color-mix(in srgb, var(--accent, #f97316) 15%, transparent);
  border-left: 3px solid var(--accent, #f97316);
  color: var(--text);
  font-size: 11px;
  padding: 6px 12px;
  line-height: 1.4;
}
</style>

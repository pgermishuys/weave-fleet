<script setup lang="ts">
import { computed } from 'vue'
import { MessageSquare, Check, SkipForward, AlertCircle, Loader2 } from 'lucide-vue-next'
import type { ReviewCommentQueueItem } from '@/stores/review-comment-queue'

const props = defineProps<{
  items: ReviewCommentQueueItem[]
}>()

const emit = defineEmits<{
  approve: [item: ReviewCommentQueueItem]
  skip: [item: ReviewCommentQueueItem]
  updateReply: [item: ReviewCommentQueueItem, reply: string]
  approveAll: []
}>()

const pendingItems = computed(() => props.items.filter((i) => i.status === 'pending'))
const doneItems = computed(() => props.items.filter((i) => i.status !== 'pending'))

function onReplyInput(item: ReviewCommentQueueItem, event: Event): void {
  const value = (event.target as HTMLTextAreaElement).value
  emit('updateReply', item, value)
}
</script>

<template>
  <section
    v-if="items.length > 0"
    class="rcq-panel"
    aria-label="Review comment queue"
  >
    <header class="rcq-header">
      <MessageSquare
        class="rcq-header-icon"
        :size="14"
        aria-hidden="true"
      />
      <span class="rcq-header-title">Review Comments</span>
      <span
        v-if="pendingItems.length > 0"
        class="rcq-badge"
        :aria-label="`${pendingItems.length} pending`"
      >{{ pendingItems.length }}</span>
      <button
        v-if="pendingItems.length > 1"
        type="button"
        class="rcq-approve-all-btn"
        title="Approve all with current reply text"
        @click="emit('approveAll')"
      >
        Approve all
      </button>
    </header>

    <ul
      class="rcq-list"
      aria-label="Pending review comments"
    >
      <li
        v-for="item in pendingItems"
        :key="item.id"
        class="rcq-item"
      >
        <div class="rcq-item-meta">
          <span
            class="rcq-item-location"
            :title="`${item.path}${item.line ? ':' + item.line : ''}`"
          >{{ item.path }}{{ item.line ? ':' + item.line : '' }}</span>
          <span class="rcq-item-author">@{{ item.authorLogin }}</span>
        </div>

        <blockquote class="rcq-item-body">
          {{ item.originalBody.length > 120 ? item.originalBody.slice(0, 120) + '…' : item.originalBody }}
        </blockquote>

        <textarea
          class="rcq-item-reply"
          :value="item.proposedReply"
          placeholder="Type your reply…"
          rows="3"
          :aria-label="`Reply to comment on ${item.path}`"
          @input="onReplyInput(item, $event)"
        />

        <div class="rcq-item-actions">
          <button
            type="button"
            class="rcq-btn rcq-btn--approve"
            :disabled="!item.proposedReply.trim()"
            :title="item.proposedReply.trim() ? 'Post reply and resolve thread' : 'Enter a reply first'"
            @click="emit('approve', item)"
          >
            <Check :size="12" aria-hidden="true" />
            Approve
          </button>
          <button
            type="button"
            class="rcq-btn rcq-btn--skip"
            title="Skip without replying"
            @click="emit('skip', item)"
          >
            <SkipForward :size="12" aria-hidden="true" />
            Skip
          </button>
          <a
            v-if="item.commentUrl && item.commentUrl.startsWith('https://github.com/')"
            :href="item.commentUrl"
            class="rcq-item-link"
            target="_blank"
            rel="noopener noreferrer"
            title="View on GitHub"
          >↗</a>
        </div>
      </li>
    </ul>

    <ul
      v-if="doneItems.length > 0"
      class="rcq-list rcq-list--done"
      aria-label="Processed review comments"
    >
      <li
        v-for="item in doneItems"
        :key="item.id"
        class="rcq-item rcq-item--done"
      >
        <div class="rcq-item-meta">
          <span class="rcq-item-location">{{ item.path }}{{ item.line ? ':' + item.line : '' }}</span>
          <span
            v-if="item.status === 'resolved'"
            class="rcq-status rcq-status--resolved"
            aria-label="Resolved"
          ><Check :size="11" aria-hidden="true" /> Resolved</span>
          <span
            v-else-if="item.status === 'replying'"
            class="rcq-status rcq-status--replying"
            aria-label="Posting reply…"
          ><Loader2 :size="11" class="rcq-spin" aria-hidden="true" /> Posting…</span>
          <span
            v-else-if="item.status === 'skipped'"
            class="rcq-status rcq-status--skipped"
            aria-label="Skipped"
          ><SkipForward :size="11" aria-hidden="true" /> Skipped</span>
          <span
            v-else-if="item.status === 'failed'"
            class="rcq-status rcq-status--failed"
            :title="item.error"
            aria-label="Failed"
          ><AlertCircle :size="11" aria-hidden="true" /> Failed</span>
        </div>
      </li>
    </ul>
  </section>
</template>

<style scoped>
.rcq-panel {
  border-top: 1px solid var(--border);
  padding: 8px 12px 12px;
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.rcq-header {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
  font-weight: 600;
  color: var(--text);
}

.rcq-header-icon {
  color: var(--muted);
  flex-shrink: 0;
}

.rcq-header-title {
  flex: 1;
  min-width: 0;
}

.rcq-badge {
  background: var(--accent, #f97316);
  color: #fff;
  border-radius: 8px;
  font-size: 10px;
  font-weight: 700;
  padding: 1px 5px;
  line-height: 1.4;
}

.rcq-approve-all-btn {
  background: none;
  border: 1px solid var(--border);
  border-radius: 4px;
  cursor: pointer;
  font-size: 10px;
  padding: 2px 6px;
  color: var(--muted);
}

.rcq-approve-all-btn:hover {
  color: var(--text);
  border-color: var(--text);
}

.rcq-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.rcq-list--done {
  gap: 2px;
  margin-top: 4px;
  border-top: 1px solid var(--border);
  padding-top: 6px;
}

.rcq-item {
  display: flex;
  flex-direction: column;
  gap: 4px;
  background: var(--bg-subtle, var(--panel-bg));
  border: 1px solid var(--border);
  border-radius: 6px;
  padding: 8px 10px;
}

.rcq-item--done {
  background: none;
  border: none;
  border-radius: 0;
  padding: 2px 0;
}

.rcq-item-meta {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
}

.rcq-item-location {
  font-size: 10px;
  font-family: monospace;
  color: var(--text);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  max-width: 160px;
}

.rcq-item-author {
  font-size: 10px;
  color: var(--muted);
  flex-shrink: 0;
}

.rcq-item-body {
  margin: 0;
  padding: 4px 8px;
  border-left: 2px solid var(--border);
  font-size: 10px;
  color: var(--muted);
  line-height: 1.4;
  font-style: italic;
}

.rcq-item-reply {
  width: 100%;
  box-sizing: border-box;
  resize: vertical;
  font-size: 11px;
  line-height: 1.4;
  padding: 5px 7px;
  border: 1px solid var(--border);
  border-radius: 4px;
  background: var(--bg, var(--panel-bg));
  color: var(--text);
  font-family: inherit;
}

.rcq-item-reply:focus {
  outline: none;
  border-color: var(--accent, #f97316);
}

.rcq-item-actions {
  display: flex;
  align-items: center;
  gap: 6px;
}

.rcq-btn {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  border-radius: 4px;
  cursor: pointer;
  font-size: 11px;
  padding: 3px 8px;
  border: 1px solid transparent;
  font-family: inherit;
}

.rcq-btn:disabled {
  opacity: 0.4;
  cursor: not-allowed;
}

.rcq-btn--approve {
  background: var(--accent, #f97316);
  color: #fff;
}

.rcq-btn--approve:hover:not(:disabled) {
  opacity: 0.88;
}

.rcq-btn--skip {
  background: none;
  border-color: var(--border);
  color: var(--muted);
}

.rcq-btn--skip:hover {
  color: var(--text);
  border-color: var(--text);
}

.rcq-item-link {
  margin-left: auto;
  font-size: 11px;
  color: var(--muted);
  text-decoration: none;
}

.rcq-item-link:hover {
  color: var(--text);
}

.rcq-status {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  font-size: 10px;
  margin-left: auto;
}

.rcq-status--resolved {
  color: #22c55e;
}

.rcq-status--skipped {
  color: var(--muted);
}

.rcq-status--failed {
  color: #ef4444;
}

.rcq-status--replying {
  color: var(--muted);
}

@keyframes spin {
  from { transform: rotate(0deg); }
  to { transform: rotate(360deg); }
}

.rcq-spin {
  animation: spin 1s linear infinite;
}
</style>

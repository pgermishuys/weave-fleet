<script setup lang="ts">
import { computed, onUnmounted, ref } from 'vue'
import {
  CircleDot,
  CircleCheck,
  CircleDashed,
  GitMerge,
  GitPullRequest,
  GitPullRequestClosed,
  CheckCircle2,
  XCircle,
  Clock,
  Minus,
  ChevronDown,
  ChevronRight,
  ExternalLink,
  MessageSquare,
  LoaderCircle,
  X,
} from 'lucide-vue-next'
import type { SmartLink, CiStatus, CheckRun, ReviewThreadSummary } from './types'
import { refreshSingleLink } from './composables/use-smart-links'

const props = defineProps<{ link: SmartLink; sessionId: string | null }>()
const emit = defineEmits<{ dismiss: [linkId: string] }>()

const refreshing = ref(false)

let hoverTimer: ReturnType<typeof setTimeout> | undefined

function onMouseEnter(): void {
  if (props.link.isTerminal || props.link.isDismissed) return
  hoverTimer = setTimeout(async () => {
    if (props.link.isTerminal || props.link.isDismissed || !props.sessionId) return
    refreshing.value = true
    try {
      await refreshSingleLink(props.sessionId, props.link.url)
    } finally {
      refreshing.value = false
    }
  }, 1000)
}

function onMouseLeave(): void {
  clearTimeout(hoverTimer)
  hoverTimer = undefined
}

onUnmounted(() => {
  clearTimeout(hoverTimer)
})

const ciExpanded = ref(false)

const statusIcon = computed(() => {
  if (props.link.resourceType === 'pull_request') {
    switch (props.link.status) {
      case 'merged': return GitMerge
      case 'closed': return GitPullRequestClosed
      case 'draft': return GitPullRequest
      default: return GitPullRequest
    }
  }
  // Issues
  switch (props.link.status) {
    case 'closed': return CircleCheck
    case 'open': return CircleDot
    default: return CircleDashed
  }
})

const statusClass = computed(() => {
  switch (props.link.status) {
    case 'merged': return 'smart-link-icon smart-link-icon--merged'
    case 'closed': return 'smart-link-icon smart-link-icon--closed'
    case 'open': return 'smart-link-icon smart-link-icon--open'
    case 'draft': return 'smart-link-icon smart-link-icon--draft'
    default: return 'smart-link-icon'
  }
})

interface LinkLabel {
  name: string
  color: string
}

const labels = computed<LinkLabel[]>(() => {
  const meta = props.link.metadata
  if (meta && Array.isArray(meta.labels)) {
    return meta.labels as LinkLabel[]
  }
  return []
})

const HEX_COLOR_RE = /^[0-9a-fA-F]{3,6}$/

const ciStatus = computed<CiStatus | null>(() => {
  const meta = props.link.metadata
  if (props.link.resourceType !== 'pull_request' || !meta?.ci) return null
  return meta.ci as CiStatus
})

const ciIcon = computed(() => {
  const s = ciStatus.value?.ciStatus
  if (!s || s === 'none') return null
  if (s === 'success') return CheckCircle2
  if (s === 'failure') return XCircle
  if (s === 'pending') return Clock
  return Minus
})

const ciIconClass = computed(() => {
  const s = ciStatus.value?.ciStatus
  if (!s || s === 'none') return ''
  if (s === 'success') return 'ci-badge ci-badge--success'
  if (s === 'failure') return 'ci-badge ci-badge--failure'
  if (s === 'pending') return 'ci-badge ci-badge--pending'
  return 'ci-badge ci-badge--neutral'
})

const ciLabel = computed(() => {
  const s = ciStatus.value?.ciStatus
  if (!s || s === 'none') return ''
  if (s === 'success') return 'CI passed'
  if (s === 'failure') return 'CI failed'
  if (s === 'pending') return 'CI running'
  return 'CI neutral'
})

const checkRuns = computed<CheckRun[]>(() => ciStatus.value?.checkRuns ?? [])

function checkRunIcon(cr: CheckRun) {
  if (cr.status !== 'completed') return Clock
  if (cr.conclusion === 'success') return CheckCircle2
  if (cr.conclusion === 'failure' || cr.conclusion === 'timed_out' || cr.conclusion === 'startup_failure') return XCircle
  return Minus
}

function checkRunIconClass(cr: CheckRun): string {
  if (cr.status !== 'completed') return 'cr-icon cr-icon--pending'
  if (cr.conclusion === 'success') return 'cr-icon cr-icon--success'
  if (cr.conclusion === 'failure' || cr.conclusion === 'timed_out' || cr.conclusion === 'startup_failure') return 'cr-icon cr-icon--failure'
  return 'cr-icon cr-icon--neutral'
}

const unresolvedCount = computed(() => {
  const meta = props.link.metadata
  if (props.link.resourceType !== 'pull_request' || !meta?.reviewThreads) return 0
  return (meta.reviewThreads as ReviewThreadSummary).unresolvedCount ?? 0
})

function getLabelStyle(color: string): { backgroundColor: string; borderColor: string; color: string } {
  const safeColor = HEX_COLOR_RE.test(color) ? color : '888888'
  return {
    backgroundColor: `#${safeColor}22`,
    borderColor: `#${safeColor}55`,
    color: `#${safeColor}`,
  }
}
</script>

<template>
  <article
    class="smart-link-item"
    @mouseenter="onMouseEnter"
    @mouseleave="onMouseLeave"
  >
    <component
      :is="statusIcon"
      :class="statusClass"
      :size="15"
      aria-hidden="true"
    />

    <div class="smart-link-body">
      <div class="smart-link-title-row">
        <a
          class="smart-link-title"
          :href="link.url"
          target="_blank"
          rel="noopener noreferrer"
          @click.stop
        >
          {{ link.title || link.url }}
        </a>

        <!-- Review comment count badge -->
        <span
          v-if="unresolvedCount > 0"
          class="review-badge"
          :title="`${unresolvedCount} unresolved review comment${unresolvedCount === 1 ? '' : 's'}`"
        >
          <MessageSquare :size="12" aria-hidden="true" />
          <span class="review-badge-count">{{ unresolvedCount }}</span>
        </span>
      </div>

      <!-- CI aggregate badge -->
      <div v-if="ciIcon || refreshing" class="ci-status-row">
        <LoaderCircle
          v-if="refreshing"
          class="refreshing-spinner"
          :size="13"
          aria-label="Refreshing"
        />
        <button
          v-if="checkRuns.length > 0 && !refreshing"
          type="button"
          class="ci-badge-btn"
          :aria-label="ciLabel"
          :title="ciLabel"
          @click.stop="ciExpanded = !ciExpanded"
        >
          <component
            :is="ciIcon"
            :class="ciIconClass"
            :size="13"
            aria-hidden="true"
          />
          <component
            :is="ciExpanded ? ChevronDown : ChevronRight"
            class="ci-chevron"
            :size="10"
            aria-hidden="true"
          />
        </button>
        <component
          v-else-if="ciIcon && !refreshing"
          :is="ciIcon"
          :class="ciIconClass"
          :size="13"
          :aria-label="ciLabel"
          :title="ciLabel"
          aria-hidden="false"
        />
      </div>

      <div
        v-if="labels.length > 0"
        class="smart-link-labels"
      >
        <span
          v-for="label in labels"
          :key="label.name"
          class="smart-link-label"
          :style="getLabelStyle(label.color)"
        >
          {{ label.name }}
        </span>
      </div>

      <!-- Expandable check-run list -->
      <ul
        v-if="ciExpanded && checkRuns.length > 0"
        class="check-run-list"
        aria-label="CI check runs"
      >
        <li
          v-for="cr in checkRuns"
          :key="cr.id"
          class="check-run-item"
        >
          <component
            :is="checkRunIcon(cr)"
            :class="checkRunIconClass(cr)"
            :size="11"
            aria-hidden="true"
          />
          <span class="cr-name" :title="cr.name">{{ cr.name }}</span>
          <a
            v-if="cr.htmlUrl"
            :href="cr.htmlUrl"
            class="cr-link"
            target="_blank"
            rel="noopener noreferrer"
            :aria-label="`Open ${cr.name} on GitHub`"
            @click.stop
          >
            <ExternalLink :size="10" aria-hidden="true" />
          </a>
        </li>
      </ul>
    </div>

    <button
      type="button"
      class="smart-link-dismiss"
      :aria-label="`Dismiss ${link.title || link.url}`"
      @click.stop="emit('dismiss', link.id)"
    >
      <X
        :size="12"
        aria-hidden="true"
      />
    </button>
  </article>
</template>

<style scoped>
.smart-link-item {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  padding: 6px 8px;
}

.smart-link-icon {
  flex-shrink: 0;
  margin-top: 2px;
  color: var(--muted);
}

.smart-link-icon--open {
  color: #22c55e;
}

.smart-link-icon--merged {
  color: #a855f7;
}

.smart-link-icon--closed {
  color: var(--muted);
}

.smart-link-icon--draft {
  color: #f59e0b;
}

.smart-link-body {
  flex: 1;
  min-width: 0;
}

.smart-link-title-row {
  display: flex;
  align-items: center;
  gap: 4px;
  min-width: 0;
}

.smart-link-title {
  display: block;
  flex: 1;
  min-width: 0;
  margin: 0;
  font-size: 12px;
  font-weight: 500;
  color: var(--text);
  text-decoration: none;
  word-wrap: break-word;
}

.smart-link-title:hover {
  text-decoration: underline;
}

/* CI badge */
.ci-badge {
  flex-shrink: 0;
}

.ci-badge--success {
  color: #22c55e;
}

.ci-badge--failure {
  color: #ef4444;
}

.ci-badge--pending {
  color: #f59e0b;
}

.ci-badge--neutral {
  color: var(--muted);
}

.ci-status-row {
  display: flex;
  align-items: center;
  margin-top: 4px;
  margin-left: -23px; /* align CI glyph with PR icon (15px icon + 8px gap) */
}

.ci-badge-btn {
  display: inline-flex;
  align-items: center;
  gap: 2px;
  flex-shrink: 0;
  padding: 1px 2px;
  border: 0;
  border-radius: 3px;
  background: transparent;
  cursor: pointer;
}

.ci-badge-btn:hover,
.ci-badge-btn:focus-visible {
  background: rgba(255, 255, 255, 0.06);
  outline: none;
}

.ci-chevron {
  color: var(--muted);
}

/* Review comment badge */
.review-badge {
  display: inline-flex;
  align-items: center;
  gap: 2px;
  flex-shrink: 0;
  color: var(--muted);
  font-size: 11px;
}

.review-badge-count {
  font-weight: 600;
  line-height: 1;
}

/* Labels */
.smart-link-labels {
  display: flex;
  align-items: center;
  gap: 4px;
  flex-wrap: wrap;
  margin-top: 4px;
}

.smart-link-label {
  display: inline-flex;
  align-items: center;
  min-height: 18px;
  padding: 0 6px;
  border: 1px solid transparent;
  border-radius: 999px;
  font-size: 10px;
  font-weight: 600;
}

/* Check run list */
.check-run-list {
  list-style: none;
  margin: 4px 0 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.check-run-item {
  display: flex;
  align-items: center;
  gap: 4px;
  font-size: 11px;
  color: var(--muted);
}

.cr-icon {
  flex-shrink: 0;
}

.cr-icon--success { color: #22c55e; }
.cr-icon--failure { color: #ef4444; }
.cr-icon--pending { color: #f59e0b; }
.cr-icon--neutral { color: var(--muted); }

.cr-name {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  color: var(--text);
}

.cr-link {
  flex-shrink: 0;
  color: var(--muted);
  text-decoration: none;
  display: inline-flex;
  align-items: center;
}

.cr-link:hover {
  color: var(--text);
}

/* Refreshing spinner */
.refreshing-spinner {
  color: var(--muted);
  animation: spin 1s linear infinite;
}

@keyframes spin {
  from { transform: rotate(0deg); }
  to { transform: rotate(360deg); }
}

/* Dismiss button */
.smart-link-dismiss {
  flex-shrink: 0;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 20px;
  height: 20px;
  margin-top: 1px;
  border: 0;
  border-radius: 4px;
  background: transparent;
  color: var(--muted);
  cursor: pointer;
  opacity: 0;
}

.smart-link-item:hover .smart-link-dismiss {
  opacity: 1;
}

.smart-link-dismiss:hover,
.smart-link-dismiss:focus-visible {
  background: rgba(255, 255, 255, 0.08);
  color: var(--text);
  opacity: 1;
  outline: none;
}
</style>

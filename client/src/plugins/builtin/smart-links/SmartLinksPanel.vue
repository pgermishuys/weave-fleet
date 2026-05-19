<script setup lang="ts">
import { computed } from 'vue'
import { Link } from 'lucide-vue-next'
import { useSessionsStore } from '@/stores/sessions'
import { useSmartLinksStore } from '@/stores/smart-links'
import { apiFetch } from '@/lib/api-client'
import SmartLinkItem from './SmartLinkItem.vue'
import { secondsUntilRefresh, isRefreshing, refreshNow, POLL_INTERVAL_SECONDS } from './composables/use-smart-links'

const sessionsStore = useSessionsStore()
const smartLinksStore = useSmartLinksStore()

const sessionId = computed(() => sessionsStore.activeSessionId)
const activeLinks = computed(() =>
  sessionId.value ? smartLinksStore.getActiveLinks(sessionId.value) : [],
)

// Circular arc countdown: r=7, circumference = 2π*7 ≈ 43.98
const ARC_RADIUS = 7
const ARC_CIRCUMFERENCE = 2 * Math.PI * ARC_RADIUS
const arcOffset = computed(() =>
  ARC_CIRCUMFERENCE * (1 - secondsUntilRefresh.value / POLL_INTERVAL_SECONDS),
)

async function handleRefreshNow(): Promise<void> {
  await refreshNow()
}

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
      <button
        type="button"
        class="refresh-timer-btn"
        :aria-label="isRefreshing ? 'Refreshing…' : `Refresh now (next refresh in ${secondsUntilRefresh}s)`"
        :title="isRefreshing ? 'Refreshing…' : `Refresh now (next refresh in ${secondsUntilRefresh}s)`"
        :disabled="isRefreshing"
        @click="handleRefreshNow"
      >
        <svg
          class="refresh-arc"
          :class="{ 'refresh-arc--spinning': isRefreshing }"
          width="18"
          height="18"
          viewBox="0 0 18 18"
          aria-hidden="true"
        >
          <!-- Track circle -->
          <circle
            class="arc-track"
            cx="9"
            cy="9"
            :r="ARC_RADIUS"
            fill="none"
            stroke-width="2"
          />
          <!-- Countdown arc — rotated so it starts at top -->
          <circle
            class="arc-fill"
            cx="9"
            cy="9"
            :r="ARC_RADIUS"
            fill="none"
            stroke-width="2"
            :stroke-dasharray="ARC_CIRCUMFERENCE"
            :stroke-dashoffset="arcOffset"
            stroke-linecap="round"
            transform="rotate(-90 9 9)"
          />
        </svg>
      </button>
    </header>

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
  flex: 1;
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

/* Refresh arc timer button */
.refresh-timer-btn {
  flex-shrink: 0;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 24px;
  height: 24px;
  padding: 0;
  border: 0;
  border-radius: 4px;
  background: transparent;
  cursor: pointer;
  color: var(--muted);
}

.refresh-timer-btn:hover:not(:disabled),
.refresh-timer-btn:focus-visible:not(:disabled) {
  background: rgba(255, 255, 255, 0.06);
  color: var(--text);
  outline: none;
}

.refresh-timer-btn:disabled {
  cursor: default;
}

.refresh-arc {
  display: block;
}

.arc-track {
  stroke: rgba(255, 255, 255, 0.1);
}

.arc-fill {
  stroke: currentColor;
  transition: stroke-dashoffset 0.9s linear;
}

.refresh-arc--spinning .arc-fill {
  animation: arc-spin 1s linear infinite;
  stroke-dashoffset: 11; /* ~quarter arc visible */
}

@keyframes arc-spin {
  from { transform: rotate(-90deg); transform-origin: 9px 9px; }
  to { transform: rotate(270deg); transform-origin: 9px 9px; }
}
</style>

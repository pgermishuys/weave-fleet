<script setup lang="ts">
import { computed } from 'vue'
import { Link } from 'lucide-vue-next'
import { useSessionsStore } from '@/stores/sessions'
import { useSmartLinksStore } from '@/stores/smart-links'
import { apiFetch } from '@/lib/api-client'
import { refreshSingleLink } from './composables/use-smart-links'
import SmartLinkItem from './SmartLinkItem.vue'

const sessionsStore = useSessionsStore()
const smartLinksStore = useSmartLinksStore()

const sessionId = computed(() => sessionsStore.activeSessionId)
const activeLinks = computed(() =>
  sessionId.value ? smartLinksStore.getActiveLinks(sessionId.value) : [],
)

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

function handleRefresh(url: string): void {
  const sid = sessionId.value
  if (!sid) return
  void refreshSingleLink(sid, url)
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
        @dismiss="handleDismiss"
        @refresh="handleRefresh"
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
</style>

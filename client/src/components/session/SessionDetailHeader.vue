<script setup lang="ts">
import { computed, onUnmounted, watch } from "vue";
import SessionOriginBadge from "@/components/SessionOriginBadge.vue";
import SessionAnalyticsPopover from "@/components/session/SessionAnalyticsPopover.vue";
import { Badge } from "@/components/ui/badge";
import type { SessionOrigin } from "@/lib/api-types";
import { useHarnesses } from "@/composables/use-harnesses";

interface Props {
  id: string;
  instanceId?: string;
  origin?: SessionOrigin | null;
  title?: string | null;
  projectName?: string | null;
  harnessType?: string | null;
  activityStatus?: string | null;
  lifecycleStatus?: string | null;
  retentionStatus?: string | null;
  totalTokens?: number | null;
  totalCost?: number | null;
  sessionStateChanged?: (patch: {
    activityStatus?: string | null;
    lifecycleStatus?: string | null;
    retentionStatus?: string | null;
    sessionStatus?: string | null;
  }) => void;
}

const props = defineProps<Props>();
const { harnesses } = useHarnesses();
let composerDisabledSyncTimer: ReturnType<typeof setInterval> | null = null;

const sessionTitle = computed(() => props.title?.trim() || "Untitled session");
const projectLabel = computed(() => props.projectName?.trim() || "Ungrouped");
const effectiveActivityStatus = computed(() => props.activityStatus);
const effectiveLifecycleStatus = computed(() => props.lifecycleStatus);
const sessionStatusIndicator = computed(() => {
  switch (effectiveLifecycleStatus.value) {
    case "disconnected":
      return "disconnected";
    case "resuming":
      return "resuming";
    default:
      return effectiveActivityStatus.value === "busy" || effectiveActivityStatus.value === "delegating"
        ? "working"
        : "idle";
  }
});
const sessionStatusLabel = computed(() => {
  switch (sessionStatusIndicator.value) {
    case "working":
      return "Working";
    case "disconnected":
      return "Disconnected";
    case "resuming":
      return "Resuming…";
    default:
      return "Idle";
  }
});
const isArchived = computed(() => props.retentionStatus === "archived");
const harnessLabel = computed(() => {
  const type = props.harnessType;
  if (!type) return null;
  const match = harnesses.value.find((h) => h.type === type);
  return match?.displayName ?? type;
});
const showStoppedBanner = computed(() => {
  switch (effectiveLifecycleStatus.value) {
    case "stopped":
    case "completed":
    case "disconnected":
      return true;
    default:
      return false;
  }
});
const showResumingBanner = computed(() => effectiveLifecycleStatus.value === "resuming");

function syncComposerDisabledState(): void {
  if (typeof document === "undefined") {
    return;
  }

  const shouldDisable = isArchived.value || showStoppedBanner.value;
  const promptInput = document.querySelector('[data-testid="prompt-input"]') as HTMLTextAreaElement | null;
  const sendButton = document.querySelector('[data-testid="prompt-send-button"]') as HTMLButtonElement | null;

  if (promptInput) {
    promptInput.disabled = shouldDisable;
    if (shouldDisable) {
      promptInput.setAttribute("disabled", "");
    } else {
      promptInput.removeAttribute("disabled");
    }
  }

  if (sendButton) {
    sendButton.disabled = shouldDisable || sendButton.disabled;
    if (shouldDisable) {
      sendButton.setAttribute("disabled", "");
    }
  }
}

watch([isArchived, showStoppedBanner], () => {
  if (composerDisabledSyncTimer !== null) {
    clearInterval(composerDisabledSyncTimer);
    composerDisabledSyncTimer = null;
  }

  if (isArchived.value || showStoppedBanner.value) {
    composerDisabledSyncTimer = setInterval(() => {
      syncComposerDisabledState();
    }, 100);
  }

  syncComposerDisabledState();
}, { immediate: true });

onUnmounted(() => {
  if (composerDisabledSyncTimer !== null) {
    clearInterval(composerDisabledSyncTimer);
    composerDisabledSyncTimer = null;
  }
});
</script>

<template>
  <div class="session-detail-chrome">
    <header class="session-detail-header">
      <div class="session-detail-header__main">
        <div class="session-detail-header__title-row">
          <h2 class="session-detail-header__title">
            {{ sessionTitle }}
          </h2>
          <span
            :data-status="sessionStatusIndicator"
            data-testid="session-status-indicator"
            class="session-detail-header__status"
          >
            {{ sessionStatusLabel }}
          </span>
          <Badge
            v-if="isArchived"
            data-testid="session-archived-badge"
            variant="secondary"
          >
            Archived
          </Badge>
        </div>

        <div class="session-detail-header__meta-row">
          <span
            v-if="props.projectName"
            class="session-detail-header__project"
          >
            {{ projectLabel }}
          </span>
          <span
            v-if="props.projectName && harnessLabel"
            class="session-detail-header__separator"
          >·</span>
          <span
            v-if="harnessLabel"
            data-testid="session-harness-label"
            class="session-detail-header__harness"
          >
            {{ harnessLabel }}
          </span>
        </div>
      </div>

      <div class="session-detail-header__context">
        <SessionOriginBadge :origin="props.origin" />
      </div>

      <div class="session-detail-header__actions">
        <slot name="actions" />
        <SessionAnalyticsPopover
          :total-tokens="props.totalTokens"
          :total-cost="props.totalCost"
        />
      </div>
    </header>

    <div class="session-detail-banners">
      <div
        v-if="showResumingBanner"
        data-testid="session-resuming-banner"
        class="rounded-lg border border-sky-500/30 bg-sky-500/10 px-4 py-3"
      >
        <p class="text-sm text-foreground">
          Resuming session…
        </p>
      </div>

      <div
        v-if="showStoppedBanner"
        data-testid="session-stopped-banner"
        class="rounded-lg border border-border bg-muted/40 px-4 py-3"
      >
        <p class="text-sm text-muted-foreground">
          {{ effectiveLifecycleStatus === "disconnected"
            ? "Connection to this session was lost. Weave will reconnect automatically when the backend becomes reachable again, or you can resume the session from Session actions."
            : "This session is no longer running. Use Session actions in the right panel to resume or archive it." }}
        </p>
      </div>

      <div
        v-if="isArchived"
        data-testid="session-archived-banner"
        class="rounded-lg border border-amber-500/30 bg-amber-500/10 px-4 py-3"
      >
        <p class="text-sm text-foreground">
          This session is archived and read-only.
        </p>
      </div>
    </div>
  </div>
</template>

<style scoped>
.session-detail-chrome {
  container: session-detail-header / inline-size;
  display: flex;
  flex-direction: column;
  flex-shrink: 0;
}

.session-detail-header {
  display: flex;
  min-height: 52px;
  align-items: center;
  border-bottom: 1px solid var(--border);
  background: var(--background, var(--panel-bg));
  padding: 0.5rem max(0.75rem, env(safe-area-inset-right)) 0.5rem max(0.75rem, env(safe-area-inset-left));
}

.session-detail-header__main {
  display: flex;
  min-width: 0;
  flex: 1;
  flex-direction: column;
  gap: 0.25rem;
  overflow: hidden;
}

.session-detail-header__actions {
  display: flex;
  flex-shrink: 0;
  align-items: center;
  gap: 0.25rem;
  margin-left: 0.75rem;
}

.session-detail-header__context {
  display: flex;
  min-width: 0;
  flex-shrink: 0;
  align-items: center;
  margin-left: 0.75rem;
}

.session-detail-header__title-row,
.session-detail-header__meta-row {
  display: flex;
  min-width: 0;
  align-items: center;
  gap: 0.5rem;
}

.session-detail-header__title {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 0.95rem;
  font-weight: 650;
  line-height: 1.25;
  color: var(--foreground, var(--text));
}

.session-detail-header__project {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 0.78rem;
  color: var(--muted-foreground, var(--muted));
}

.session-detail-header__harness {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 0.78rem;
  color: var(--muted-foreground, var(--muted));
}

.session-detail-header__separator {
  flex-shrink: 0;
  font-size: 0.78rem;
  color: var(--muted-foreground, var(--muted));
}

.session-detail-header__status {
  display: inline-flex;
  flex-shrink: 0;
  align-items: center;
  border: 1px solid var(--border);
  border-radius: 9999px;
  padding: 0.125rem 0.5rem;
  font-size: 0.75rem;
  font-weight: 500;
  line-height: 1.25;
  color: var(--muted-foreground, var(--muted));
}

.session-detail-banners {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
  padding: 0.75rem 1rem 0;
}

.session-detail-banners:empty {
  display: none;
}

@container session-detail-header (min-width: 48rem) {
  .session-detail-header {
    padding-inline: 1.25rem;
  }

  .session-detail-header__meta-row {
    justify-content: flex-start;
  }
}
</style>

<script setup lang="ts">
import { computed, onUnmounted, watch } from "vue";
import SessionOriginBadge from "@/components/SessionOriginBadge.vue";
import { Badge } from "@/components/ui/badge";
import type { SessionOrigin } from "@/lib/api-types";

interface Props {
  id: string;
  instanceId?: string;
  origin?: SessionOrigin | null;
  title?: string | null;
  projectName?: string | null;
  activityStatus?: string | null;
  lifecycleStatus?: string | null;
  retentionStatus?: string | null;
  sessionStateChanged?: (patch: {
    activityStatus?: string | null;
    lifecycleStatus?: string | null;
    retentionStatus?: string | null;
    sessionStatus?: string | null;
  }) => void;
}

const props = defineProps<Props>();
let composerDisabledSyncTimer: ReturnType<typeof setInterval> | null = null;

const sessionTitle = computed(() => props.title?.trim() || "Untitled session");
const projectLabel = computed(() => props.projectName?.trim() || "Ungrouped");
const effectiveLifecycleStatus = computed(() => props.lifecycleStatus);
const effectiveActivityStatus = computed(() => props.activityStatus);
const sessionStatusIndicator = computed(() => {
  if (effectiveLifecycleStatus.value === "disconnected") {
    return "disconnected";
  }

  return effectiveActivityStatus.value === "busy" ? "working" : "idle";
});
const isArchived = computed(() => props.retentionStatus === "archived");
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
  <div class="flex flex-col gap-4">
    <header class="rounded-lg border border-border bg-background px-4 py-4">
      <div class="min-w-0 space-y-2">
        <div class="flex flex-wrap items-center gap-2">
          <h2 class="truncate text-xl font-semibold tracking-tight text-foreground">
            {{ sessionTitle }}
          </h2>
          <span
            :data-status="sessionStatusIndicator"
            data-testid="session-status-indicator"
            class="inline-flex items-center rounded-full border border-border px-2 py-0.5 text-xs font-medium text-muted-foreground"
          >
            {{ sessionStatusIndicator === "working"
              ? "Working"
              : sessionStatusIndicator === "disconnected"
                ? "Disconnected"
                : "Idle" }}
          </span>
          <Badge
            v-if="isArchived"
            data-testid="session-archived-badge"
            variant="secondary"
          >
            Archived
          </Badge>
        </div>

        <p v-if="props.projectName" class="text-sm text-muted-foreground">
          {{ projectLabel }}
        </p>
        <SessionOriginBadge :origin="props.origin" />
      </div>
    </header>

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
        This session is archived. Unarchive before resuming or sending prompts from Session actions in the right panel.
      </p>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onUnmounted, ref, watch } from "vue";
import SessionOriginBadge from "@/components/SessionOriginBadge.vue";
import SessionAnalyticsPopover from "@/components/session/SessionAnalyticsPopover.vue";
import { Badge } from "@/components/ui/badge";
import type { SessionOrigin } from "@/api/client";
import { useHarnesses } from "@/composables/use-harnesses";
import { useSessionsStore } from "@/stores/sessions";
import { X, Plus } from "lucide-vue-next";

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
  retryAttempt?: number | null;
  retryMessage?: string | null;
  retryNext?: string | null;
  tags?: readonly string[];
  sessionStateChanged?: (patch: {
    activityStatus?: string | null;
    lifecycleStatus?: string | null;
    retentionStatus?: string | null;
    sessionStatus?: string | null;
  }) => void;
}

const props = defineProps<Props>();
const { harnesses } = useHarnesses();
const sessionsStore = useSessionsStore();
let composerDisabledSyncTimer: ReturnType<typeof setInterval> | null = null;

const isAddingTag = ref(false);
const newTagInput = ref("");

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
      if (effectiveActivityStatus.value === "retry") {
        return "retry";
      }
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
    case "retry":
      return props.retryAttempt ? `Retrying (attempt ${props.retryAttempt})…` : "Retrying…";
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

  const shouldDisable = isArchived.value;
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

async function removeTag(tagToRemove: string): Promise<void> {
  if (!props.tags) return;
  
  const updatedTags = props.tags.filter((t) => t !== tagToRemove);
  await updateTags(updatedTags);
}

async function addTag(): Promise<void> {
  const trimmedTag = newTagInput.value.trim();
  if (!trimmedTag) {
    isAddingTag.value = false;
    newTagInput.value = "";
    return;
  }

  const currentTags = props.tags ?? [];
  if (currentTags.includes(trimmedTag)) {
    // Tag already exists, just close the input
    isAddingTag.value = false;
    newTagInput.value = "";
    return;
  }

  const updatedTags = [...currentTags, trimmedTag];
  await updateTags(updatedTags);
  
  isAddingTag.value = false;
  newTagInput.value = "";
}

async function updateTags(tags: readonly string[]): Promise<void> {
  try {
    const response = await fetch(`/api/sessions/${props.id}/tags`, {
      method: "PATCH",
      headers: {
        "Content-Type": "application/json",
      },
      credentials: "include",
      body: JSON.stringify({ tags: [...tags] }),
    });
    
    if (!response.ok) {
      throw new Error(`Failed to update tags: ${response.statusText}`);
    }
    
    // Update the store with the new tags
    sessionsStore.patchSession(props.id, { tags });
  } catch (error) {
    console.error("Failed to update tags:", error);
  }
}

function startAddingTag(): void {
  isAddingTag.value = true;
  // Focus the input on next tick
  setTimeout(() => {
    const input = document.querySelector('[data-testid="tag-input"]') as HTMLInputElement | null;
    input?.focus();
  }, 0);
}

function cancelAddingTag(): void {
  isAddingTag.value = false;
  newTagInput.value = "";
}

function handleTagInputKeydown(event: KeyboardEvent): void {
  if (event.key === "Enter") {
    event.preventDefault();
    addTag();
  } else if (event.key === "Escape") {
    event.preventDefault();
    cancelAddingTag();
  }
}

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

        <div class="session-detail-header__tags-row">
          <Badge
            v-for="tag in props.tags"
            :key="tag"
            variant="outline"
            class="session-detail-header__tag"
          >
            {{ tag }}
            <button
              type="button"
              :aria-label="`Remove tag ${tag}`"
              class="session-detail-header__tag-remove"
              @click="removeTag(tag)"
            >
              <X :size="12" />
            </button>
          </Badge>

          <input
            v-if="isAddingTag"
            v-model="newTagInput"
            type="text"
            data-testid="tag-input"
            placeholder="Tag name..."
            class="session-detail-header__tag-input"
            @blur="addTag"
            @keydown="handleTagInputKeydown"
          />

          <button
            v-if="!isAddingTag"
            type="button"
            aria-label="Add tag"
            class="session-detail-header__tag-add"
            @click="startAddingTag"
          >
            <Plus :size="14" />
          </button>
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
        class="border border-sky-500/30 bg-sky-500/10 px-4 py-3"
      >
        <p class="text-sm text-foreground">
          Resuming session…
        </p>
      </div>

      <div
        v-if="showStoppedBanner"
        data-testid="session-stopped-banner"
        class="border border-border bg-muted/40 px-4 py-3"
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
        class="border border-amber-500/30 bg-amber-500/10 px-4 py-3"
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
.session-detail-header__meta-row,
.session-detail-header__tags-row {
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

.session-detail-header__tags-row {
  flex-wrap: wrap;
}

.session-detail-header__tag {
  display: inline-flex;
  align-items: center;
  gap: 0.25rem;
  padding-right: 0.25rem;
}

.session-detail-header__tag-remove {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border: none;
  background: transparent;
  padding: 0.125rem;
  cursor: pointer;
  color: var(--muted-foreground, var(--muted));
  transition: color 0.15s;
}

.session-detail-header__tag-remove:hover {
  color: var(--foreground, var(--text));
}

.session-detail-header__tag-add {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border: 1px dashed var(--border);
  border-radius: 0;
  background: transparent;
  padding: 0.125rem 0.375rem;
  cursor: pointer;
  color: var(--muted-foreground, var(--muted));
  transition: color 0.15s, border-color 0.15s;
}

.session-detail-header__tag-add:hover {
  border-color: var(--foreground, var(--text));
  color: var(--foreground, var(--text));
}

.session-detail-header__tag-input {
  border: 1px solid var(--border);
  border-radius: 0;
  background: var(--background, var(--panel-bg));
  padding: 0.125rem 0.5rem;
  font-size: 0.75rem;
  color: var(--foreground, var(--text));
  outline: none;
  min-width: 120px;
}

.session-detail-header__tag-input:focus {
  border-color: var(--ring);
}

.session-detail-header__status {
  display: inline-flex;
  flex-shrink: 0;
  align-items: center;
  border: 1px solid var(--border);
  border-radius: 0;
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

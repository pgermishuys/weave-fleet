<script setup lang="ts">
import { computed, nextTick, onUnmounted, ref, shallowRef, useTemplateRef, watch } from "vue";
import { storeToRefs } from "pinia";
import SessionContextChips from "@/components/session-context/SessionContextChips.vue";
import SessionAnalyticsPopover from "@/components/session/SessionAnalyticsPopover.vue";
import StatusGlyph from "@/components/sessions/StatusGlyph.vue";
import { Badge } from "@/components/ui/badge";
import type { SessionOrigin } from "@/api/client";
import { useHarnesses } from "@/composables/use-harnesses";
import { useModels } from "@/composables/use-models";
import { modelDisplayName } from "@/lib/agent-model-choice";
import { useSessionsStore } from "@/stores/sessions";
import { useSidebarStore } from "@/stores/sidebar";
import { ArchiveRestore, GitBranch, Layers, Loader2, X, Plus } from "lucide-vue-next";

interface Props {
  id: string;
  instanceId?: string;
  origin?: SessionOrigin | null;
  title?: string | null;
  projectName?: string | null;
  harnessType?: string | null;
  /** The profile the session started with; it keeps it for as long as it lives. */
  harnessProfileName?: string | null;
  activityStatus?: string | null;
  lifecycleStatus?: string | null;
  retentionStatus?: string | null;
  totalTokens?: number | null;
  totalCost?: number | null;
  retryAttempt?: number | null;
  retryMessage?: string | null;
  retryNext?: string | null;
  directory?: string | null;
  branch?: string | null;
  tags?: readonly string[];
  /** Whether the title is an input; the ⋯ menu's Rename sets it, as does double-clicking the title. */
  editingTitle?: boolean;
  renameDisabled?: boolean;
  canRestore?: boolean;
  isRestoring?: boolean;
  sessionStateChanged?: (patch: {
    activityStatus?: string | null;
    lifecycleStatus?: string | null;
    retentionStatus?: string | null;
    sessionStatus?: string | null;
  }) => void;
}

const props = defineProps<Props>();
const emit = defineEmits<{
  "update:editingTitle": [editing: boolean];
  rename: [title: string];
  restore: [];
}>();
const { harnesses } = useHarnesses();
const { models } = useModels(() => props.id);
const sessionsStore = useSessionsStore();
const { sessions } = storeToRefs(sessionsStore);
const { sessionListShown } = storeToRefs(useSidebarStore());
let composerDisabledSyncTimer: ReturnType<typeof setInterval> | null = null;

const isAddingTag = ref(false);
const newTagInput = ref("");

const sessionTitle = computed(() => props.title?.trim() || "Untitled session");

const titleDraft = shallowRef("");
const titleInput = useTemplateRef<HTMLInputElement>("titleInput");
let titleEditHandled = false;

watch(() => props.editingTitle, async (editing) => {
  if (!editing) {
    return;
  }

  titleDraft.value = props.title?.trim() ?? "";
  titleEditHandled = false;
  await nextTick();
  titleInput.value?.focus();
  titleInput.value?.select();
}, { immediate: true });

function startTitleEdit(): void {
  if (props.renameDisabled || isArchived.value) {
    return;
  }

  emit("update:editingTitle", true);
}

function finishTitleEdit(save: boolean): void {
  if (titleEditHandled) {
    return;
  }

  titleEditHandled = true;
  const nextTitle = titleDraft.value.trim();
  emit("update:editingTitle", false);
  if (save && nextTitle && nextTitle !== (props.title?.trim() ?? "")) {
    emit("rename", nextTitle);
  }
}

function handleTitleInputKeydown(event: KeyboardEvent): void {
  if (event.key === "Enter") {
    event.preventDefault();
    finishTitleEdit(true);
  } else if (event.key === "Escape") {
    event.preventDefault();
    finishTitleEdit(false);
  }
}

function handleTitleKeydown(event: KeyboardEvent): void {
  if (event.key === "F2") {
    event.preventDefault();
    startTitleEdit();
  }
}
const projectLabel = computed(() => props.projectName?.trim() || "Ungrouped");
const effectiveActivityStatus = computed(() => props.activityStatus);
const effectiveLifecycleStatus = computed(() => props.lifecycleStatus);
const sessionStatusIndicator = computed(() => {
  switch (effectiveLifecycleStatus.value) {
    case "disconnected":
      return "disconnected";
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
    case "retry":
      return props.retryAttempt ? `Retrying (attempt ${props.retryAttempt})…` : "Retrying…";
    default:
      return "Idle";
  }
});
// The session row owns the visible status. The header repeats the row's glyph
// only when no sessions list is on screen, and names a retry because the row
// has no room for why the session is stalled.
const glyphStatus = computed(() => {
  if (sessionStatusIndicator.value === "working" || sessionStatusIndicator.value === "retry") {
    return "active";
  }
  switch (effectiveLifecycleStatus.value) {
    case "disconnected":
    case "completed":
    case "stopped":
    case "error":
      return effectiveLifecycleStatus.value;
    default:
      return "idle";
  }
});
const retryNote = computed(() => {
  if (sessionStatusIndicator.value !== "retry") return null;
  return props.retryAttempt ? `Retrying · attempt ${props.retryAttempt}` : "Retrying";
});
const isArchived = computed(() => props.retentionStatus === "archived");
const harnessLabel = computed(() => {
  const type = props.harnessType;
  if (!type) return null;
  const match = harnesses.value.find((h) => h.type === type);
  return match?.displayName ?? type;
});
// The model the next prompt will get: the session's own choice, else whatever answered last.
const modelLabel = computed(() => {
  const session = sessions.value.find((candidate) => candidate.session.id === props.id);
  const modelId = session?.selectedModel?.modelID ?? session?.lastAssistantModelId;
  return modelDisplayName(modelId, models.value) || null;
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
          <StatusGlyph
            v-if="!sessionListShown"
            data-testid="session-header-glyph"
            :status="glyphStatus"
            :activity="effectiveActivityStatus"
            :label="sessionStatusLabel"
          />
          <input
            v-if="props.editingTitle"
            ref="titleInput"
            v-model="titleDraft"
            type="text"
            spellcheck="false"
            aria-label="Session name"
            placeholder="Session name"
            data-testid="session-title-input"
            class="session-detail-header__title-input"
            @blur="finishTitleEdit(true)"
            @keydown="handleTitleInputKeydown"
          >
          <h2
            v-else
            class="session-detail-header__title"
            :class="{ 'session-detail-header__title--editable': !props.renameDisabled && !isArchived }"
            :title="!props.renameDisabled && !isArchived ? 'Double-click to rename' : undefined"
            :tabindex="!props.renameDisabled && !isArchived ? 0 : undefined"
            data-testid="session-title"
            @dblclick="startTitleEdit"
            @keydown="handleTitleKeydown"
          >
            {{ sessionTitle }}
          </h2>
          <span
            v-if="retryNote"
            data-testid="session-retry-note"
            class="session-detail-header__retry"
          >
            {{ retryNote }}
          </span>
          <span
            :data-status="sessionStatusIndicator"
            data-testid="session-status-indicator"
            role="status"
            class="sr-only"
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
          <template v-if="props.harnessProfileName">
            <span class="session-detail-header__separator">·</span>
            <span
              data-testid="session-profile-label"
              class="session-detail-header__profile"
              :title="`Started with the ${props.harnessProfileName} profile, which it keeps`"
            >
              <Layers
                :size="11"
                aria-hidden="true"
              />
              {{ props.harnessProfileName }}
            </span>
          </template>
          <template v-if="modelLabel">
            <span
              v-if="props.projectName || harnessLabel || props.harnessProfileName"
              class="session-detail-header__separator"
            >·</span>
            <span
              data-testid="session-model-label"
              class="session-detail-header__model"
              title="The model the next prompt will get"
            >
              {{ modelLabel }}
            </span>
          </template>
          <template v-if="props.branch">
            <span
              v-if="props.projectName || harnessLabel"
              class="session-detail-header__separator"
            >·</span>
            <span
              data-testid="session-branch"
              class="session-detail-header__branch"
              :title="`Branch ${props.branch}`"
            >
              <GitBranch
                :size="12"
                aria-hidden="true"
              />
              <span class="session-detail-header__branch-name">{{ props.branch }}</span>
            </span>
          </template>
          <span
            v-if="props.directory && (props.projectName || harnessLabel || props.branch)"
            class="session-detail-header__separator"
          >·</span>
          <span
            v-if="props.directory"
            class="session-detail-header__directory"
            :title="props.directory"
          >
            {{ props.directory }}
          </span>

          <span class="session-detail-header__tags-row">
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
              title="Add tag"
              class="session-detail-header__tag-add"
              @click="startAddingTag"
            >
              <Plus :size="12" />
            </button>
          </span>
        </div>
      </div>

      <div class="session-detail-header__context">
        <SessionContextChips
          :session-id="props.id"
          :origin="props.origin"
        />
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
        v-if="showStoppedBanner"
        data-testid="session-stopped-banner"
        class="border border-border bg-muted/40 px-4 py-3"
      >
        <p class="text-sm text-muted-foreground">
          {{ effectiveLifecycleStatus === "disconnected"
            ? "Connection to this session was lost. Weave will reconnect automatically when the backend becomes reachable again, or send a message to reconnect now."
            : "This session isn't running. Send a message to pick it up again." }}
        </p>
      </div>

      <div
        v-if="isArchived"
        data-testid="session-archived-banner"
        class="session-archived-banner"
      >
        <p class="session-archived-banner__copy">
          This session is archived and read-only.
        </p>
        <button
          v-if="props.canRestore"
          type="button"
          class="session-archived-banner__restore"
          data-testid="session-restore-button"
          :disabled="props.isRestoring"
          @click="emit('restore')"
        >
          <Loader2
            v-if="props.isRestoring"
            class="session-archived-banner__spinner"
            aria-hidden="true"
          />
          <ArchiveRestore
            v-else
            aria-hidden="true"
          />
          Restore
        </button>
      </div>
    </div>
  </div>
</template>

<style scoped>
.session-archived-banner {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 8px 16px;
  border-bottom: 1px solid color-mix(in srgb, var(--status-waiting) 30%, transparent);
  background: color-mix(in srgb, var(--status-waiting) 10%, transparent);
}

.session-archived-banner__copy {
  flex: 1;
  margin: 0;
  color: var(--text);
  font-size: 13px;
}

.session-archived-banner__restore {
  display: inline-flex;
  flex-shrink: 0;
  align-items: center;
  gap: 6px;
  height: 28px;
  padding: 0 12px;
  border: 1px solid var(--border);
  border-radius: 999px;
  background: var(--panel-bg);
  color: var(--text);
  font-size: 12px;
  font-weight: 500;
  cursor: pointer;
  transition: background var(--transition);
}

.session-archived-banner__restore:hover:not(:disabled) {
  background: color-mix(in srgb, var(--text) 5%, var(--panel-bg));
}

.session-archived-banner__restore:disabled {
  opacity: 0.6;
  cursor: default;
}

.session-archived-banner__restore svg {
  width: 13px;
  height: 13px;
}

.session-archived-banner__spinner {
  animation: session-archived-spin 0.8s linear infinite;
}

@keyframes session-archived-spin {
  to { transform: rotate(360deg); }
}

.session-detail-header__title--editable {
  margin-inline: -6px;
  padding-inline: 6px;
  border-radius: 6px;
  cursor: text;
  transition: background var(--transition);
}

.session-detail-header__title--editable:hover {
  background: color-mix(in srgb, var(--text) 5%, transparent);
}

.session-detail-header__title--editable:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 0;
}

/* The input takes the title's place at the title's size, so nothing shifts. */
.session-detail-header__title-input {
  flex: 1 1 auto;
  min-width: 0;
  max-width: 480px;
  margin-inline: -7px;
  padding: 0 6px;
  border: 1px solid var(--accent);
  border-radius: 6px;
  background: var(--card-bg);
  box-shadow: 0 0 0 3px var(--accent-dim);
  color: var(--text);
  font: inherit;
  font-size: 14px;
  font-weight: 600;
  letter-spacing: -0.005em;
  line-height: 1.3;
  outline: none;
}

.session-detail-chrome {
  container: session-detail-header / inline-size;
  display: flex;
  flex-direction: column;
  flex-shrink: 0;
}

/* Two lines: title with status, then one muted line of context. */
.session-detail-header {
  display: flex;
  min-height: 56px;
  align-items: center;
  border-bottom: 1px solid var(--border);
  background: transparent;
  padding: 8px max(1rem, env(safe-area-inset-right)) 8px max(1rem, env(safe-area-inset-left));
}

.session-detail-header__main {
  display: flex;
  min-width: 0;
  flex: 1;
  flex-direction: column;
  gap: 3px;
  overflow: hidden;
  /* Room for the rename field's ring, which would otherwise be clipped; the margins cancel it out. */
  margin: -4px -8px;
  padding: 4px 8px;
}

.session-detail-header__actions {
  display: flex;
  flex-shrink: 0;
  align-items: center;
  gap: 2px;
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
  gap: 6px;
}

.session-detail-header__title {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 14px;
  font-weight: 600;
  letter-spacing: -0.005em;
  line-height: 1.3;
  color: var(--text);
}

.session-detail-header__meta-row {
  font-size: 12px;
  line-height: 1.4;
  color: var(--muted);
  white-space: nowrap;
}

.session-detail-header__project,
.session-detail-header__harness,
.session-detail-header__model {
  flex-shrink: 0;
  max-width: 14rem;
  overflow: hidden;
  text-overflow: ellipsis;
}

/* The profile reads as a tag on the harness: accent text, findable without competing with the title. */
.session-detail-header__profile {
  display: inline-flex;
  flex-shrink: 0;
  align-items: center;
  gap: 4px;
  max-width: 12rem;
  overflow: hidden;
  color: var(--accent);
  text-overflow: ellipsis;
  white-space: nowrap;
}

.session-detail-header__separator {
  flex-shrink: 0;
  opacity: 0.6;
}

/* The branch keeps its width (long names truncate at 18ch); the directory gives way first. */
.session-detail-header__branch {
  display: inline-flex;
  flex: 0 0 auto;
  align-items: center;
  gap: 4px;
  min-width: 0;
  max-width: calc(18ch + 16px);
  color: var(--text);
  font-family: var(--font-mono-stack);
  font-size: 11.5px;
}

.session-detail-header__branch-name {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
}

.session-detail-header__branch svg {
  flex-shrink: 0;
  color: var(--muted);
}

.session-detail-header__directory {
  flex: 0 1 auto;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  font-family: var(--font-mono-stack);
  font-size: 11.5px;
  opacity: 0.8;
}

.session-detail-header__tags-row {
  flex-shrink: 0;
  gap: 4px;
  margin-left: 4px;
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
  color: var(--muted);
  transition: color var(--transition);
}

.session-detail-header__tag-remove:hover {
  color: var(--text);
}

.session-detail-header__tag-add {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 20px;
  height: 20px;
  border: 1px dashed color-mix(in srgb, var(--muted) 45%, transparent);
  border-radius: calc(var(--radius-btn) - 2px);
  background: transparent;
  padding: 0;
  cursor: pointer;
  color: var(--muted);
  opacity: 0.7;
  transition: color var(--transition), border-color var(--transition), opacity var(--transition);
}

.session-detail-header__tag-add:hover {
  border-color: var(--text);
  color: var(--text);
  opacity: 1;
}

.session-detail-header__tag-input {
  border: 1px solid var(--border);
  border-radius: calc(var(--radius-btn) - 2px);
  background: var(--card-bg);
  padding: 1px 8px;
  font-size: 12px;
  color: var(--text);
  outline: none;
  min-width: 120px;
}

.session-detail-header__tag-input:focus {
  border-color: var(--ring, var(--accent));
}

.session-detail-header__retry {
  flex-shrink: 0;
  font-size: 12px;
  font-weight: 500;
  line-height: 1.4;
  color: var(--status-waiting);
  white-space: nowrap;
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

/* Clear the fixed menu button that replaces the sidebar on narrow screens. */
@media (max-width: 716px) {
  .session-detail-header {
    padding-left: 44px;
  }
}

@container session-detail-header (min-width: 48rem) {
  .session-detail-header {
    padding-inline: 1.25rem;
  }
}
</style>

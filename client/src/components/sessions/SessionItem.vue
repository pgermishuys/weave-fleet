<script setup lang="ts">
import { computed, nextTick, shallowRef, useTemplateRef } from "vue";
import StatusGlyph from "./StatusGlyph.vue";
import ProgressRing from "./ProgressRing.vue";
import { useRouter } from "@tanstack/vue-router";
import {
  Archive,
  ArchiveRestore,
  Check,
  Copy,
  FolderOpen,
  GitFork,
  Pencil,
  Repeat,
  Sparkles,
  Trash2,
} from "lucide-vue-next";
import {
  ContextMenu,
  ContextMenuContent,
  ContextMenuItem,
  ContextMenuSeparator,
  ContextMenuSub,
  ContextMenuSubContent,
  ContextMenuSubTrigger,
  ContextMenuTrigger,
} from "@/components/ui/context-menu";
import {
  useDeleteSession,
  useForkSession,
  useMoveSession,
  useRenameSession,
} from "@/composables/use-session-actions";
import { useProjects } from "@/composables/use-projects";
import { useAutomationsNav } from "@/composables/use-automations-nav";
import { useEnabledHarnesses } from "@/composables/use-enabled-harnesses";
import { useWorkflowsFeature } from "@/composables/use-workflows-feature";
import { useWorkflowsNav } from "@/composables/use-workflows-nav";
import { DRAFT_COST_NOTE } from "@/lib/workflow-draft";
import type { SessionListItem } from "@/api/client";
import { sessionCache } from "@/lib/session-cache";
import { dispatchSessionRemoved } from "@/lib/session-sync";
import { isSessionLive, sessionRowDim, sessionRowStatus } from "@/lib/session-row-status";
import { useRelativeTime } from "@/composables/use-relative-time";
import { useSessionsStore } from "@/stores/sessions";
import { useArchiveQueueStore } from "@/stores/archive-queue";
import { useSessionSelectionStore } from "@/stores/session-selection";
import OpenToolContextSubmenu from "@/components/sessions/OpenToolContextSubmenu.vue";
import ConfirmDeleteSessionDialog from "./ConfirmDeleteSessionDialog.vue";

interface Props {
  session: SessionListItem;
  active: boolean;
  /** Shown instead of the title: a workflow step's name under its run. */
  label?: string;
  /** Shown instead of the row's status: "With you" on a workflow step the user finishes, while it's open. */
  stepNote?: string;
}

interface Emits {
  select: [session: SessionListItem];
  dragSessionStart: [sessionId: string, projectId: string | null];
  dragSessionEnd: [];
}

const props = defineProps<Props>();
const emit = defineEmits<Emits>();
const sessionsStore = useSessionsStore();
const archiveQueue = useArchiveQueueStore();
const selection = useSessionSelectionStore();
const router = useRouter();
const { startCreateFromSession } = useAutomationsNav();
const { isWorkflowsEnabled } = useWorkflowsFeature();
const { harnesses } = useEnabledHarnesses();
const workflowsNav = useWorkflowsNav();

const isInlineEditing = shallowRef(false);
const isContextMenuOpen = shallowRef(false);
const isDeleteDialogOpen = shallowRef(false);
const isRestoring = shallowRef(false);
const renameDraft = shallowRef("");
const initialRenameTitle = shallowRef("");
const hasHandledInlineRename = shallowRef(false);
const inlineTitleRef = useTemplateRef<HTMLInputElement>("inlineTitle");

const {
  renameSession,
  isLoading: isRenaming,
} = useRenameSession();
const {
  forkSession,
  isForking,
  forkingSessionId,
} = useForkSession();
const {
  moveSession,
  isMoving,
} = useMoveSession();
const {
  deleteSession,
  isDeleting,
} = useDeleteSession();
const {
  projects,
  isLoading: isProjectsLoading,
} = useProjects({
  enabled: computed(() => isContextMenuOpen.value),
});

const sessionId = computed(() => props.session.session.id);
const instanceId = computed(() => props.session.instanceId);
const rawTitle = computed(() => props.session.session.title ?? "");
const displayTitle = computed(() => props.label || props.session.session.title?.trim() || "Untitled session");
const now = useRelativeTime();
const rowStatus = computed(() => sessionRowStatus(props.session, now.value));
const isLive = computed(() => isSessionLive(props.session));
// The open session never dims.
const rowDim = computed(() => (props.active ? 0 : sessionRowDim(props.session, now.value)));
const progress = computed(() => {
  const summary = props.session.progress;
  if (!summary || summary.total <= 0) return null;
  // A quiet session that finished its list shows nothing; its age comes back.
  if (rowStatus.value.tone === "quiet" && summary.done >= summary.total) return null;
  return summary;
});
// Only a working session's count replaces its age. A quiet session with unfinished items keeps the ring next to its
// age, without the number; words the user has to act on stay next to the ring.
const showProgressCount = computed(() => progress.value !== null && rowStatus.value.tone === "working");
const progressDescription = computed(() => {
  const summary = progress.value;
  if (!summary) return "";
  const counts = `${summary.done} of ${summary.total} done`;
  return summary.current ? `${counts}. Now: ${summary.current}` : counts;
});
const isArchivedSession = computed(() => props.session.retentionStatus === "archived");
const fallbackCanArchive = computed(() => !isArchivedSession.value);
// The retention state wins over capabilities, which only refresh with the list.
const canArchive = computed(() => !isArchivedSession.value && (props.session.capabilities?.canArchive ?? fallbackCanArchive.value));
const canRestore = computed(() => isArchivedSession.value);
const isSelected = computed(() => selection.isSelected(sessionId.value));
const canFork = computed(() => props.session.capabilities?.canFork ?? true);
const canDelete = computed(() => props.session.capabilities?.canDelete ?? true);
const isForkingCurrentSession = computed(() => isForking.value && forkingSessionId.value === sessionId.value);
/**
 * Save as workflow… asks the session's harness off the record, as the recap does, so only a harness that can shows
 * it. Decided by the capability, not the harness's name.
 */
const canSaveAsWorkflow = computed(() => isWorkflowsEnabled.value
  && !isArchivedSession.value
  && harnesses.value.find((harness) => harness.type === (props.session.harnessType ?? "opencode"))?.capabilities.supportsOffTheRecordPrompt === true);
const isAnyActionPending = computed(() =>
  isRestoring.value
  || isDeleting.value
  || isForkingCurrentSession.value
  || isMoving.value
  || isRenaming.value
);

const isDraggable = computed(() => !isInlineEditing.value && !isAnyActionPending.value);
const isDragging = shallowRef(false);

function handleDragStart(event: DragEvent): void {
  if (!isDraggable.value || !event.dataTransfer) {
    event.preventDefault();
    return;
  }

  event.dataTransfer.effectAllowed = "move";
  // Safari requires at least one setData call or the drag is cancelled
  event.dataTransfer.setData("text/plain", sessionId.value);
  event.dataTransfer.setData("application/weave-session-id", sessionId.value);
  event.dataTransfer.setData("application/weave-source-project-id", props.session.projectId ?? "");
  isDragging.value = true;
  emit("dragSessionStart", sessionId.value, props.session.projectId ?? null);
}

function handleDragEnd(): void {
  isDragging.value = false;
  emit("dragSessionEnd");
}

const projectTargets = computed(() => {
  const targets = projects.value.map((project) => ({
    id: project.type === "scratch" ? null : project.id,
    label: project.type === "scratch" ? "Ungrouped" : project.name,
  }));

  if (!targets.some((target) => target.id === null)) {
    targets.unshift({
      id: null,
      label: "Ungrouped",
    });
  }

  return targets;
});


function handleSelect(event: MouseEvent): void {
  if (isInlineEditing.value) {
    return;
  }

  // ⌘/Ctrl-click and Shift-click pick rows; while any are picked, a plain click adds or removes one.
  if (event.shiftKey) {
    selection.extendTo(sessionId.value, sessionsStore.activeSessionId);
    return;
  }

  if (event.metaKey || event.ctrlKey || selection.isSelecting) {
    selection.toggle(sessionId.value);
    return;
  }

  emit("select", props.session);
}

function handleRowKeydown(event: KeyboardEvent): void {
  if (event.key === "F2") {
    event.preventDefault();
    startRename();
  }
}

function handleArchive(): void {
  isContextMenuOpen.value = false;
  archiveQueue.archive([sessionId.value]);
}

async function handleRestore(): Promise<void> {
  isContextMenuOpen.value = false;
  isRestoring.value = true;
  try {
    await archiveQueue.restore(sessionId.value);
  } catch {
    // The archive queue shows the error.
  } finally {
    isRestoring.value = false;
  }
}

function handleContextMenuOpenChange(value: boolean): void {
  isContextMenuOpen.value = value;
}

function startRename(): void {
  isContextMenuOpen.value = false;
  renameDraft.value = rawTitle.value;
  initialRenameTitle.value = rawTitle.value;
  hasHandledInlineRename.value = false;
  isInlineEditing.value = true;

  void focusInlineTitle();
}

async function focusInlineTitle(): Promise<void> {
  await nextTick();

  const focusAndSelect = (): boolean => {
    const inlineTitle = inlineTitleRef.value;
    if (!inlineTitle) {
      return false;
    }

    inlineTitle.focus({ preventScroll: true });
    inlineTitle.setSelectionRange(0, inlineTitle.value.length);
    return document.activeElement === inlineTitle;
  };

  if (focusAndSelect()) {
    return;
  }

  await new Promise<void>((resolve) => requestAnimationFrame(() => resolve()));
  if (focusAndSelect()) {
    return;
  }

  window.setTimeout(() => {
    focusAndSelect();
  }, 0);
}

function cancelRename(): void {
  if (hasHandledInlineRename.value) {
    return;
  }

  hasHandledInlineRename.value = true;
  isInlineEditing.value = false;
}

function handleInlineRenameKeydown(event: KeyboardEvent): void {
  if (event.key === "Enter") {
    event.preventDefault();
    void handleRename(renameDraft.value);
    return;
  }

  if (event.key === "Escape") {
    event.preventDefault();
    cancelRename();
  }
}

async function handleRename(nextTitle: string): Promise<void> {
  if (hasHandledInlineRename.value) {
    return;
  }

  hasHandledInlineRename.value = true;
  const trimmedTitle = nextTitle.trim();
  isInlineEditing.value = false;

  if (trimmedTitle.length === 0 || trimmedTitle === rawTitle.value.trim()) {
    return;
  }

  try {
    await renameSession(sessionId.value, trimmedTitle);
  } catch {
    // Errors are handled by the mutation composable state.
  }
}

async function handleFork(): Promise<void> {
  if (!canFork.value) {
    return;
  }

  try {
    const response = await forkSession(sessionId.value);
    await router.navigate({
      to: "/sessions/$id",
      params: { id: response.session.id },
      search: {
        instanceId: response.instanceId,
        parentSessionId: undefined,
      },
    });
  } catch {
    // Errors are handled by the mutation composable state.
  }
}

/** A new automation with this session's first message and folder; the person adds when it runs. */
function handleRepeatOnSchedule(): void {
  startCreateFromSession(sessionId.value);
  void router.navigate({ to: "/automations" });
}

function handleSaveAsWorkflow(): void {
  void router.navigate({ to: "/workflows" });
  void workflowsNav.draftFromSession(sessionId.value, displayTitle.value);
}

async function handleMove(projectId: string | null): Promise<void> {
  try {
    await moveSession(sessionId.value, projectId);
  } catch {
    // Errors are handled by the mutation composable state.
  }
}

async function handleCopySessionId(): Promise<void> {
  try {
    await navigator.clipboard.writeText(sessionId.value);
  } catch {
    // Clipboard failures are non-fatal.
  }
}

function openDeleteDialog(): void {
  if (!canDelete.value) {
    return;
  }

  isContextMenuOpen.value = false;
  isDeleteDialogOpen.value = true;
}

async function handleDelete(): Promise<void> {
  if (!canDelete.value) {
    return;
  }

  try {
    await deleteSession(sessionId.value, instanceId.value);
    isDeleteDialogOpen.value = false;
    removeSessionFromStore();
  } catch {
    // Errors are handled by the mutation composable state.
  }
}

function removeSessionFromStore(): void {
  sessionCache.delete(sessionId.value, instanceId.value);
  dispatchSessionRemoved(sessionId.value);
  sessionsStore.removeSession(sessionId.value);
}
</script>

<template>
  <ContextMenu
    :open="isContextMenuOpen"
    @update:open="handleContextMenuOpenChange"
  >
    <ContextMenuTrigger as-child>
      <div
        class="session-item-shell"
        :class="{ 'session-item-shell--dragging': isDragging }"
        data-tree-leaf
        :data-session-id="session.session.id"
        :draggable="isDraggable"
        aria-roledescription="draggable session"
        @dragstart="handleDragStart"
        @dragend="handleDragEnd"
      >
        <template v-if="!isInlineEditing">
          <button
            type="button"
            class="session-item"
            :class="{
              active,
              'session-item--selected': isSelected,
              'session-item--has-action': (canArchive || canRestore) && !selection.isSelecting,
              [`session-item--dim-${rowDim}`]: rowDim > 0,
            }"
            :aria-current="active ? 'true' : undefined"
            :aria-pressed="selection.isSelecting ? isSelected : undefined"
            title="Double-click to rename"
            data-testid="session-row"
            @click="handleSelect"
            @dblclick="startRename"
            @keydown="handleRowKeydown"
          >
            <span
              v-if="selection.isSelecting"
              class="session-check"
              :class="{ 'session-check--on': isSelected }"
              aria-hidden="true"
            >
              <Check v-if="isSelected" />
            </span>
            <StatusGlyph
              v-else-if="isLive"
              :status="session.sessionStatus"
              :activity="session.activityStatus"
              :label="rowStatus.description"
            />
            <span
              v-else
              class="session-glyph-slot"
              aria-hidden="true"
            />

            <span class="session-copy">
              <span class="session-title">{{ displayTitle }}</span>
            </span>

            <span
              v-if="progress"
              class="session-progress"
              :title="progressDescription"
            >
              <ProgressRing
                :done="progress.done"
                :total="progress.total"
              />
              <span
                v-if="showProgressCount"
                class="session-progress__count"
                aria-hidden="true"
              >{{ progress.done }}/{{ progress.total }}</span>
              <span class="sr-only">{{ progressDescription }}</span>
            </span>

            <span
              v-if="stepNote"
              class="session-meta session-meta--with"
              data-testid="session-step-note"
            >{{ stepNote }}</span>
            <span
              v-else-if="rowStatus.label && !showProgressCount"
              class="session-meta"
              :class="`session-meta--${rowStatus.tone}`"
            >{{ rowStatus.label }}</span>
          </button>
          <button
            v-if="canRestore && !selection.isSelecting"
            type="button"
            class="session-row-action"
            :aria-label="`Restore ${displayTitle}`"
            title="Restore"
            data-testid="session-row-restore"
            :disabled="isAnyActionPending"
            @click.stop="handleRestore"
          >
            <ArchiveRestore aria-hidden="true" />
          </button>
          <button
            v-else-if="canArchive && !selection.isSelecting"
            type="button"
            class="session-row-action"
            :aria-label="`Archive ${displayTitle}`"
            title="Archive"
            data-testid="session-row-archive"
            :disabled="isAnyActionPending"
            @click.stop="handleArchive"
          >
            <Archive aria-hidden="true" />
          </button>
        </template>

        <div
          v-else
          class="session-item session-item--editing"
          :class="{ active }"
        >
          <StatusGlyph
            v-if="isLive"
            :status="session.sessionStatus"
            :activity="session.activityStatus"
            :label="rowStatus.description"
          />
          <span
            v-else
            class="session-glyph-slot"
            aria-hidden="true"
          />

          <span class="session-copy">
            <input
              ref="inlineTitle"
              v-model="renameDraft"
              type="text"
              spellcheck="false"
              aria-label="Session name"
              class="session-title session-title--editing"
              placeholder="Session name"
              @blur="handleRename(renameDraft)"
              @keydown="handleInlineRenameKeydown"
            >
          </span>
        </div>
      </div>
    </ContextMenuTrigger>

    <ContextMenuContent class="w-56">
      <ContextMenuItem
        :disabled="isAnyActionPending"
        @select="startRename"
      >
        <Pencil class="size-3.5" />
        Rename
      </ContextMenuItem>

      <ContextMenuItem
        v-if="canArchive"
        :disabled="isAnyActionPending"
        data-testid="session-context-archive"
        @select="handleArchive"
      >
        <Archive class="size-3.5" />
        Archive
      </ContextMenuItem>

      <ContextMenuItem
        v-if="canRestore"
        :disabled="isAnyActionPending"
        data-testid="session-context-restore"
        @select="handleRestore"
      >
        <ArchiveRestore class="size-3.5" />
        Restore
      </ContextMenuItem>

      <ContextMenuItem
        v-if="canFork"
        :disabled="isAnyActionPending"
        @select="handleFork"
      >
        <GitFork class="size-3.5" />
        Fork
      </ContextMenuItem>

      <ContextMenuItem
        :disabled="isAnyActionPending"
        data-testid="session-repeat-on-schedule"
        @select="handleRepeatOnSchedule"
      >
        <Repeat class="size-3.5" />
        Repeat on a schedule…
      </ContextMenuItem>

      <ContextMenuItem
        v-if="canSaveAsWorkflow"
        :disabled="isAnyActionPending"
        :title="DRAFT_COST_NOTE"
        data-testid="session-save-as-workflow"
        @select="handleSaveAsWorkflow"
      >
        <Sparkles class="size-3.5" />
        <span class="flex flex-col">
          <span>Save as workflow…</span>
          <span
            class="text-[11px] text-muted-foreground"
            data-testid="session-save-as-workflow-cost"
          >Asks the model once, from the cache</span>
        </span>
      </ContextMenuItem>

      <OpenToolContextSubmenu :directory="session.workspaceDirectory" />

      <ContextMenuSub>
        <ContextMenuSubTrigger :disabled="isAnyActionPending">
          <FolderOpen class="size-3.5" />
          Move to Project
        </ContextMenuSubTrigger>
        <ContextMenuSubContent class="w-52">
          <ContextMenuItem
            v-if="isProjectsLoading"
            disabled
          >
            Loading projects…
          </ContextMenuItem>
          <template v-else>
            <ContextMenuItem
              v-for="project in projectTargets"
              :key="project.id ?? 'ungrouped'"
              :disabled="project.id === (session.projectId ?? null)"
              @select="handleMove(project.id)"
            >
              {{ project.label }}
            </ContextMenuItem>
          </template>
        </ContextMenuSubContent>
      </ContextMenuSub>

      <ContextMenuSeparator />

      <ContextMenuItem
        :disabled="isAnyActionPending"
        @select="handleCopySessionId"
      >
        <Copy class="size-3.5" />
        Copy Session ID
      </ContextMenuItem>

      <ContextMenuItem
        v-if="canDelete"
        variant="destructive"
        :disabled="isAnyActionPending"
        @select="openDeleteDialog"
      >
        <Trash2 class="size-3.5" />
        Permanently Delete
      </ContextMenuItem>
    </ContextMenuContent>
  </ContextMenu>

  <ConfirmDeleteSessionDialog
    v-model:open="isDeleteDialogOpen"
    :is-deleting="isDeleting"
    :session-title="displayTitle"
    @confirm="handleDelete"
  />
</template>

<style scoped>
.session-item-shell {
  position: relative;
  width: 100%;
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 1px 0;
}

.session-item-shell--dragging {
  opacity: 0.4;
  pointer-events: none;
}

.session-item {
  width: 100%;
  min-width: 0;
  min-height: 32px;
  display: flex;
  align-items: center;
  gap: 9px;
  padding: 0 10px;
  cursor: pointer;
  border: 0;
  border-radius: var(--radius-btn);
  background: transparent;
  color: color-mix(in srgb, var(--text) 86%, transparent);
  text-align: left;
  transition: background var(--transition), color var(--transition);
}

.session-item--editing {
  cursor: default;
}

.session-item--selected,
.session-item--selected:hover,
.session-item--selected.active {
  background: var(--accent-dim);
  color: var(--text);
}

/* Picking rows swaps the status glyph for a checkbox in the same slot. */
.session-check {
  display: grid;
  place-items: center;
  width: 14px;
  height: 14px;
  margin-inline: -3px;
  flex-shrink: 0;
  border: 1.5px solid color-mix(in srgb, var(--muted) 70%, transparent);
  border-radius: 4px;
}

.session-check--on {
  border-color: var(--accent);
  background: var(--accent);
  color: var(--primary-foreground);
}

.session-check svg {
  width: 10px;
  height: 10px;
  stroke-width: 3;
}

/* Archive (or Restore) takes the time's place while the row is hovered. */
.session-row-action {
  position: absolute;
  top: 50%;
  right: 5px;
  display: none;
  place-items: center;
  width: 24px;
  height: 24px;
  padding: 0;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--muted);
  cursor: pointer;
  transform: translateY(-50%);
  transition: background var(--transition), color var(--transition);
}

.session-row-action svg {
  width: 14px;
  height: 14px;
}

.session-row-action:hover {
  background: color-mix(in srgb, var(--text) 8%, transparent);
  color: var(--text);
}

.session-item-shell:hover .session-row-action,
.session-row-action:focus-visible {
  display: grid;
}

.session-row-action:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
}

.session-item-shell:hover .session-item--has-action .session-meta,
.session-item-shell:hover .session-item--has-action .session-progress,
.session-item-shell:has(.session-row-action:focus-visible) .session-meta,
.session-item-shell:has(.session-row-action:focus-visible) .session-progress {
  visibility: hidden;
}

.session-item:hover {
  background: color-mix(in srgb, var(--text) 5%, transparent);
  color: var(--text);
}

.session-item--editing:hover {
  background: transparent;
}

.session-item.active {
  background: color-mix(in srgb, var(--text) 9%, transparent);
  color: var(--text);
  font-weight: 500;
}

.session-item:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
}

/* Quiet sessions fade back: after a day without activity, and further after
   three. Hover and the open session restore full contrast. */
.session-item--dim-1 {
  color: color-mix(in srgb, var(--text) 55%, transparent);
}

.session-item--dim-2 {
  color: color-mix(in srgb, var(--text) 40%, transparent);
}

.session-item--dim-1 .session-meta {
  opacity: 0.75;
}

.session-item--dim-2 .session-meta {
  opacity: 0.6;
}

.session-item:hover .session-meta {
  opacity: 1;
}

/* Idle rows show no glyph; the slot keeps titles on one left edge. */
.session-glyph-slot {
  width: 8px;
  height: 8px;
  flex-shrink: 0;
}

.session-copy {
  flex: 1 1 auto;
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.session-title {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 13px;
  line-height: 1.3;
}

.session-meta {
  flex-shrink: 0;
  font-size: 12px;
  font-weight: 400;
  line-height: 1.3;
  color: color-mix(in srgb, var(--muted) 80%, transparent);
  font-variant-numeric: tabular-nums;
  white-space: nowrap;
}

.session-meta--retry {
  color: var(--status-waiting);
}

.session-progress {
  flex-shrink: 0;
  display: inline-flex;
  align-items: center;
  gap: 6px;
}

.session-progress__count {
  font-size: 12px;
  line-height: 1.3;
  color: var(--muted);
  font-variant-numeric: tabular-nums;
  white-space: nowrap;
}

.session-meta--attention {
  padding: 1px 7px;
  border-radius: var(--radius-btn);
  background: color-mix(in srgb, var(--status-waiting) 14%, transparent);
  color: var(--status-waiting);
  font-weight: 500;
}

.session-meta--error {
  color: var(--error);
}

.session-meta--with {
  color: var(--accent);
}

.session-title--editing {
  display: block;
  width: 100%;
  min-width: 0;
  padding: 0;
  border: 0;
  background: transparent;
  color: var(--text);
  font: inherit;
  line-height: 1.2;
  cursor: text;
  caret-color: var(--text);
  outline: none;
  appearance: none;
  box-shadow: none;
}

.session-title--editing::placeholder {
  color: var(--muted);
}

.session-title--editing:focus {
  outline: none;
}


.session-inline-edit {
  width: 100%;
}

</style>

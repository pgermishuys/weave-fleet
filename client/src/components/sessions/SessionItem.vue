<script setup lang="ts">
import { computed, nextTick, shallowRef, useTemplateRef } from "vue";
import StatusGlyph from "./StatusGlyph.vue";
import { useRouter } from "@tanstack/vue-router";
import {
  Copy,
  FolderOpen,
  GitFork,
  Pencil,
  Check,
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
  useArchiveSession,
  useDeleteSession,
  useForkSession,
  useMoveSession,
  useRenameSession,
} from "@/composables/use-session-actions";
import { useProjects } from "@/composables/use-projects";
import type { SessionListItem } from "@/api/client";
import { sessionCache } from "@/lib/session-cache";
import { dispatchSessionRemoved } from "@/lib/session-sync";
import { sessionRowStatus } from "@/lib/session-row-status";
import { useRelativeTime } from "@/composables/use-relative-time";
import { useSessionsStore } from "@/stores/sessions";
import OpenToolContextSubmenu from "@/components/sessions/OpenToolContextSubmenu.vue";
import ConfirmCompleteSessionDialog from "./ConfirmCompleteSessionDialog.vue";
import ConfirmDeleteSessionDialog from "./ConfirmDeleteSessionDialog.vue";

interface Props {
  session: SessionListItem;
  active: boolean;
}

interface Emits {
  select: [session: SessionListItem];
  dragSessionStart: [sessionId: string, projectId: string | null];
  dragSessionEnd: [];
}

const props = defineProps<Props>();
const emit = defineEmits<Emits>();
const sessionsStore = useSessionsStore();
const router = useRouter();

const isInlineEditing = shallowRef(false);
const isContextMenuOpen = shallowRef(false);
const isDeleteDialogOpen = shallowRef(false);
const isCompleteDialogOpen = shallowRef(false);
const renameDraft = shallowRef("");
const initialRenameTitle = shallowRef("");
const hasHandledInlineRename = shallowRef(false);
const inlineTitleRef = useTemplateRef<HTMLInputElement>("inlineTitle");

const {
  renameSession,
  isLoading: isRenaming,
} = useRenameSession();
const {
  archiveSession,
  isArchiving,
} = useArchiveSession();
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
const displayTitle = computed(() => props.session.session.title?.trim() || "Untitled session");
const now = useRelativeTime();
const rowStatus = computed(() => sessionRowStatus(props.session, now.value));
const isArchivedSession = computed(() => props.session.retentionStatus === "archived");
const fallbackCanArchive = computed(() => !isArchivedSession.value);
const canArchive = computed(() => props.session.capabilities?.canArchive ?? fallbackCanArchive.value);
const canFork = computed(() => props.session.capabilities?.canFork ?? true);
const canDelete = computed(() => props.session.capabilities?.canDelete ?? true);
const hasWorktree = computed(() => props.session.isolationStrategy === "worktree");
const isForkingCurrentSession = computed(() => isForking.value && forkingSessionId.value === sessionId.value);
const isAnyActionPending = computed(() =>
  isArchiving.value
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


function handleSelect(): void {
  if (isInlineEditing.value) {
    return;
  }

  emit("select", props.session);
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

function openCompleteDialog(): void {
  isContextMenuOpen.value = false;
  isCompleteDialogOpen.value = true;
}

async function handleArchive(deleteWorktree: boolean): Promise<void> {
  try {
    await archiveSession(sessionId.value);
    syncSessionStore({ retentionStatus: "archived" });
    isCompleteDialogOpen.value = false;
    // TODO: if deleteWorktree, call backend to remove the worktree
    void deleteWorktree;
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

function syncSessionStore(
  patch: Partial<{
    retentionStatus: "active" | "archived";
  }>,
): void {
  sessionsStore.patchSession(sessionId.value, patch);
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
            :class="{ active }"
            :aria-current="active ? 'true' : undefined"
            @click="handleSelect"
          >
            <StatusGlyph :status="session.sessionStatus" />

            <span class="session-copy">
              <span class="session-title">{{ displayTitle }}</span>
            </span>

            <span
              v-if="rowStatus.label"
              class="session-meta"
              :class="`session-meta--${rowStatus.tone}`"
            >{{ rowStatus.label }}</span>
          </button>
        </template>

        <div
          v-else
          class="session-item session-item--editing"
          :class="{ active }"
        >
          <StatusGlyph :status="session.sessionStatus" />

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
        @select="openCompleteDialog"
      >
        <Check class="size-3.5" />
        Complete
      </ContextMenuItem>

      <ContextMenuItem
        v-if="canFork"
        :disabled="isAnyActionPending"
        @select="handleFork"
      >
        <GitFork class="size-3.5" />
        Fork
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

  <ConfirmCompleteSessionDialog
    v-model:open="isCompleteDialogOpen"
    :is-archiving="isArchiving"
    :session-title="displayTitle"
    :has-worktree="hasWorktree"
    @confirm="handleArchive"
  />
</template>

<style scoped>
.session-item-shell {
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

.session-meta--working {
  color: var(--muted);
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

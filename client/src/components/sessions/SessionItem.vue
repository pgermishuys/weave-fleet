<script setup lang="ts">
import { computed, nextTick, shallowRef, useTemplateRef } from "vue";
import { useRouter } from "@tanstack/vue-router";
import {
  Copy,
  FolderOpen,
  GitFork,
  OctagonX,
  Pencil,
  Play,
  Square,
  StopCircle,
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
  useAbortSession,
  useArchiveSession,
  useDeleteSession,
  useForkSession,
  useMoveSession,
  useRenameSession,
  useResumeSession,
  useTerminateSession,
  useUnarchiveSession,
} from "@/composables/use-session-actions";
import { useProjects } from "@/composables/use-projects";
import type { SessionListItem } from "@/lib/api-types";
import { sessionCache } from "@/lib/session-cache";
import { dispatchSessionRemoved } from "@/lib/session-sync";
import { useSessionsStore } from "@/stores/sessions";
import ConfirmDeleteSessionDialog from "./ConfirmDeleteSessionDialog.vue";

interface Props {
  session: SessionListItem;
  active: boolean;
}

interface Emits {
  select: [session: SessionListItem];
}

const props = defineProps<Props>();
const emit = defineEmits<Emits>();
const sessionsStore = useSessionsStore();
const router = useRouter();

const isInlineEditing = shallowRef(false);
const isContextMenuOpen = shallowRef(false);
const isDeleteDialogOpen = shallowRef(false);
const renameDraft = shallowRef("");
const initialRenameTitle = shallowRef("");
const hasHandledInlineRename = shallowRef(false);
const inlineTitleRef = useTemplateRef<HTMLInputElement>("inlineTitle");

const {
  renameSession,
  isLoading: isRenaming,
} = useRenameSession();
const {
  abortSession,
  isAborting,
} = useAbortSession();
const {
  terminateSession,
  isTerminating,
} = useTerminateSession();
const {
  resumeSession,
  isResuming,
  resumingSessionId,
} = useResumeSession();
const {
  archiveSession,
  isArchiving,
} = useArchiveSession();
const {
  unarchiveSession,
  isUnarchiving,
} = useUnarchiveSession();
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

const isRunningSession = computed(() => props.session.lifecycleStatus === "running");
const isBusySession = computed(() => props.session.activityStatus === "busy");
const isArchivedSession = computed(() => props.session.retentionStatus === "archived");
const canInterrupt = computed(() => isRunningSession.value && isBusySession.value);
const canStop = computed(() => isRunningSession.value);
const canResume = computed(() => {
  switch (props.session.lifecycleStatus) {
    case "stopped":
    case "completed":
    case "disconnected":
      return true;
    default:
      return false;
  }
});
const canArchive = computed(() => !isArchivedSession.value && !isRunningSession.value);
const canUnarchive = computed(() => isArchivedSession.value);
const isForkingCurrentSession = computed(() => isForking.value && forkingSessionId.value === sessionId.value);
const isResumingCurrentSession = computed(() => isResuming.value && resumingSessionId.value === sessionId.value);
const isAnyActionPending = computed(() =>
  isAborting.value
  || isArchiving.value
  || isDeleting.value
  || isForkingCurrentSession.value
  || isMoving.value
  || isRenaming.value
  || isResumingCurrentSession.value
  || isTerminating.value
  || isUnarchiving.value,
);

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

const statusLabel = computed(() => {
  switch (props.session.sessionStatus) {
    case "completed":
      return "Completed";
    case "idle":
      return "Idle";
    case "stopped":
      return "Stopped";
    case "disconnected":
      return "Disconnected";
    case "error":
      return "Error";
    case "waiting_input":
      return "Waiting for input";
    case "active":
    default:
      return "Active";
  }
});

const statusColor = computed(() => {
  switch (props.session.sessionStatus) {
    case "completed":
      return "var(--complete)";
    case "idle":
      return "var(--idle)";
    case "stopped":
    case "disconnected":
      return "var(--muted)";
    case "error":
      return "var(--error)";
    case "waiting_input":
      return "var(--queued)";
    case "active":
    default:
      return "var(--running)";
  }
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

async function handleInterrupt(): Promise<void> {
  try {
    await abortSession(sessionId.value, instanceId.value);
    syncSessionStore({ activityStatus: "idle", sessionStatus: "idle" });
  } catch {
    // Errors are handled by the mutation composable state.
  }
}

async function handleStop(): Promise<void> {
  try {
    await terminateSession(sessionId.value, instanceId.value);
    syncSessionStore({ activityStatus: "idle", lifecycleStatus: "stopped", sessionStatus: "stopped" });
  } catch {
    // Errors are handled by the mutation composable state.
  }
}

async function handleResume(): Promise<void> {
  try {
    await resumeSession(sessionId.value);
    syncSessionStore({ activityStatus: "idle", lifecycleStatus: "running", sessionStatus: "idle" });
  } catch {
    // Errors are handled by the mutation composable state.
  }
}

async function handleArchive(): Promise<void> {
  try {
    await archiveSession(sessionId.value);
    syncSessionStore({ retentionStatus: "archived" });
  } catch {
    // Errors are handled by the mutation composable state.
  }
}

async function handleUnarchive(): Promise<void> {
  try {
    await unarchiveSession(sessionId.value);
    syncSessionStore({ retentionStatus: "active" });
  } catch {
    // Errors are handled by the mutation composable state.
  }
}

async function handleFork(): Promise<void> {
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
  isContextMenuOpen.value = false;
  isDeleteDialogOpen.value = true;
}

async function handleDelete(): Promise<void> {
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
    activityStatus: "busy" | "idle";
    lifecycleStatus: "running" | "stopped" | "completed" | "disconnected" | "error";
    retentionStatus: "active" | "archived";
    sessionStatus: "active" | "idle" | "stopped" | "completed" | "disconnected" | "error" | "waiting_input";
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
        data-tree-leaf
        :data-session-id="session.session.id"
      >
        <button
          v-if="!isInlineEditing"
          type="button"
          class="session-item"
          :class="{ active }"
          :aria-current="active ? 'true' : undefined"
          @click="handleSelect"
        >
          <span
            class="session-dot"
            :style="{ backgroundColor: statusColor }"
            aria-hidden="true"
          />

          <span class="session-copy">
            <span class="session-title">{{ displayTitle }}</span>
            <span class="session-meta">{{ statusLabel }}</span>
          </span>
        </button>

        <div
          v-else
          class="session-item session-item--editing"
          :class="{ active }"
        >
          <span
            class="session-dot"
            :style="{ backgroundColor: statusColor }"
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
            <span class="session-meta">{{ statusLabel }}</span>
          </span>
        </div>
      </div>
    </ContextMenuTrigger>

    <ContextMenuContent class="w-56">
      <ContextMenuItem
        :disabled="isAnyActionPending"
        @select="startRename"
      >
        <Pencil class="h-4 w-4" />
        Rename
      </ContextMenuItem>

      <ContextMenuItem
        v-if="canInterrupt"
        :disabled="isAnyActionPending"
        @select="handleInterrupt"
      >
        <OctagonX class="h-4 w-4" />
        Interrupt
      </ContextMenuItem>

      <ContextMenuItem
        v-if="canStop"
        :disabled="isAnyActionPending"
        @select="handleStop"
      >
        <StopCircle class="h-4 w-4" />
        Stop
      </ContextMenuItem>

      <ContextMenuItem
        v-if="canResume"
        :disabled="isAnyActionPending"
        @select="handleResume"
      >
        <Play class="h-4 w-4" />
        Resume
      </ContextMenuItem>

      <ContextMenuItem
        v-if="canArchive"
        :disabled="isAnyActionPending"
        @select="handleArchive"
      >
        <Square class="h-4 w-4" />
        Archive
      </ContextMenuItem>

      <ContextMenuItem
        v-if="canUnarchive"
        :disabled="isAnyActionPending"
        @select="handleUnarchive"
      >
        <Play class="h-4 w-4" />
        Unarchive
      </ContextMenuItem>

      <ContextMenuItem
        :disabled="isAnyActionPending"
        @select="handleFork"
      >
        <GitFork class="h-4 w-4" />
        Fork
      </ContextMenuItem>

      <ContextMenuSub>
        <ContextMenuSubTrigger :disabled="isAnyActionPending">
          <FolderOpen class="h-4 w-4" />
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
        <Copy class="h-4 w-4" />
        Copy Session ID
      </ContextMenuItem>

      <ContextMenuItem
        variant="destructive"
        :disabled="isAnyActionPending"
        @select="openDeleteDialog"
      >
        <Trash2 class="h-4 w-4" />
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
  width: 100%;
}

.session-item {
  width: 100%;
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 6px 12px 6px 38px;
  cursor: pointer;
  border: 0;
  border-left: 3px solid transparent;
  background: transparent;
  color: var(--text);
  text-align: left;
}

.session-item--editing {
  cursor: default;
}

.session-item:hover {
  background: rgba(255, 255, 255, 0.03);
}

.session-item--editing:hover {
  background: transparent;
}

.session-item.active {
  background: var(--accent-dim);
  border-left-color: var(--accent);
}

.session-item:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
}

.session-dot {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  flex-shrink: 0;
}

.session-copy {
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
  line-height: 1.2;
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

.session-meta {
  font-size: 11px;
  color: var(--muted);
}

.session-inline-edit {
  width: 100%;
}
</style>

<script setup lang="ts">
import { computed, nextTick, shallowRef, useTemplateRef } from "vue";
import StatusGlyph from "./StatusGlyph.vue";
import ProgressRing from "./ProgressRing.vue";
import { useRouter } from "@tanstack/vue-router";
import {
  Archive,
  ArchiveRestore,
  ArrowDown,
  ArrowUp,
  Check,
  ChevronRight,
  LoaderCircle,
  Copy,
  CornerDownRight,
  CornerLeftUp,
  FolderOpen,
  GitFork,
  Pencil,
  Pin,
  PinOff,
  Plus,
  Repeat,
  Sparkles,
  Trash2,
} from "lucide-vue-next";
import {
  ContextMenu,
  ContextMenuContent,
  ContextMenuHint,
  ContextMenuItem,
  ContextMenuSeparator,
  ContextMenuShortcut,
  ContextMenuSub,
  ContextMenuSubContent,
  ContextMenuSubTrigger,
  ContextMenuTrigger,
} from "@/components/ui/context-menu";
import {
  useDeleteSession,
  useForkSession,
  useMoveSession,
  useNewSessionInFolder,
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
import { useMachinesStore } from "@/stores/machines";
import { useArchiveQueueStore } from "@/stores/archive-queue";
import { useLineageMovesStore } from "@/stores/lineage-moves";
import { useSessionPinsStore } from "@/stores/session-pins";
import { isPinned, pinnedNeighbourFor } from "@/lib/session-pins";
import { movableBackUnder, movableOutOf } from "@/lib/session-lineage";
import { useSessionSelectionStore } from "@/stores/session-selection";
import OpenToolContextSubmenu from "@/components/sessions/OpenToolContextSubmenu.vue";
import PrBadge from "@/components/github/PrBadge.vue";
import { prState, prWords } from "@/lib/pr-state";
import { linkNumber, linkPrFacts } from "@/lib/smart-links";
import { useSmartLinksStore } from "@/stores/smart-links";
import ConfirmDeleteSessionDialog from "./ConfirmDeleteSessionDialog.vue";

interface Props {
  session: SessionListItem;
  active: boolean;
  /** Shown instead of the title: a workflow step's name under its run. */
  label?: string;
  /** Shown instead of the row's status: "With you" on a workflow step the user finishes, while it's open. */
  stepNote?: string;
  /** Under its parent: how it came from it ("fork", "started"), shown instead of the row's status. */
  kindLabel?: string;
  /** How much work its agent has running: the green chip. */
  runningCount?: number;
  /** Whether it has children nested under it, and whether they show. */
  hasChildren?: boolean;
  childrenExpanded?: boolean;
  /**
   * On a machine that isn't live: the machine's name. The row only opens the session there; renaming, pinning,
   * archiving, dragging and the menu wait until that machine is live, so nothing acts on the wrong machine.
   */
  openOnMachine?: string;
  /** That machine isn't answering: a working row says so instead of working on for good. */
  machineNotAnswering?: boolean;
}

interface Emits {
  select: [session: SessionListItem];
  toggleChildren: [];
  dragSessionStart: [sessionId: string, projectId: string | null];
  dragSessionEnd: [];
}

const props = defineProps<Props>();
const emit = defineEmits<Emits>();
const sessionsStore = useSessionsStore();
const archiveQueue = useArchiveQueueStore();
const lineageMoves = useLineageMovesStore();
const pins = useSessionPinsStore();
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
  startSessionInFolderOf,
  startingFromSessionId,
} = useNewSessionInFolder();
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
const rowStatus = computed(() => sessionRowStatus(props.session, now.value, !props.machineNotAnswering));
// A working row whose machine went quiet has no working glyph: it can't be known to work.
const isLive = computed(() => isSessionLive(props.session) && !(props.machineNotAnswering && props.session.sessionStatus === "active"));
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
// The pull request the session opened (or started from), once GitHub has answered for it.
const smartLinks = useSmartLinksStore();
const prBadge = computed(() => {
  const link = smartLinks.sessionPullRequest(sessionId.value);
  const number = link ? linkNumber(link) : null;
  if (!link || number === null || link.enrichmentStatus !== "resolved") return null;
  const facts = linkPrFacts(link);
  return { number, state: prState(facts), checks: facts.checks, description: `Pull request #${number} · ${prWords(facts)}` };
});
const isArchivedSession = computed(() => props.session.retentionStatus === "archived");
const readOnly = computed(() => Boolean(props.openOnMachine));
// Another machine's row has a menu of what doesn't need that machine (its folder is there, not here) once its sessions
// open in place.
const machines = useMachinesStore();
const hasMenu = computed(() => !readOnly.value || machines.opensInPlace);
const fallbackCanArchive = computed(() => !isArchivedSession.value);
// The retention state wins over capabilities, which only refresh with the list.
const canArchive = computed(() => !readOnly.value
  && !isArchivedSession.value && (props.session.capabilities?.canArchive ?? fallbackCanArchive.value));
const canRestore = computed(() => !readOnly.value && isArchivedSession.value);
const isSelected = computed(() => selection.isSelected(sessionId.value));
/** Whether rows are being picked: never on another machine's rows, which can't be archived from here. */
const isSelecting = computed(() => !readOnly.value && selection.isSelecting);
const canFork = computed(() => props.session.capabilities?.canFork ?? true);
/** Fork shows on every session that isn't archived; on a harness that can't copy a conversation it's off, with why. */
const showFork = computed(() => !isArchivedSession.value);
const forkDisabledReason = computed(() => canFork.value
  ? null
  : props.session.capabilities?.forkDisabledReason ?? "This session can't be forked.");
const canDelete = computed(() => props.session.capabilities?.canDelete ?? true);
/**
 * A fork or a session another session started can be moved out of the session it came from (and back, once out). A
 * subagent's session can't: it belongs to its parent's turn. The menu names the parent it really came from, which may not
 * be the row it sits under (the list shows everything one indent under the top-level session).
 */
function parentTitle(parentId: string): string | null {
  return sessionsStore.sessions.find((item) => item.session.id === parentId)?.session.title?.trim() || null;
}
const moveOutLabel = computed(() => {
  const link = isArchivedSession.value ? null : movableOutOf(props.session);
  if (!link) return null;
  const title = parentTitle(link.parentId);
  return title ? `Move out of "${title}"` : "Move out of its parent";
});
const moveBackLabel = computed(() => {
  const link = isArchivedSession.value ? null : movableBackUnder(props.session);
  if (!link) return null;
  const title = parentTitle(link.parentId);
  return title ? `Move back under "${title}"` : "Move back under its parent";
});
const isForkingCurrentSession = computed(() => isForking.value && forkingSessionId.value === sessionId.value);
const isStartingInFolder = computed(() => startingFromSessionId.value === sessionId.value);
/**
 * Save as workflow… asks the session's harness off the record, as the recap does, so only a harness that can shows
 * it. Decided by the capability, not the harness's name.
 */
const canSaveAsWorkflow = computed(() => isWorkflowsEnabled.value
  && !isArchivedSession.value
  && harnesses.value.find((harness) => harness.type === props.session.harnessType)?.capabilities.supportsOffTheRecordPrompt === true);
const isAnyActionPending = computed(() =>
  isRestoring.value
  || isDeleting.value
  || isForkingCurrentSession.value
  || isStartingInFolder.value
  || isMoving.value
  || isRenaming.value
);

const isDraggable = computed(() => !readOnly.value && !isInlineEditing.value && !isAnyActionPending.value);
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

const currentProjectId = computed(() => props.session.projectId ?? null);
const currentProjectLabel = computed(() =>
  projectTargets.value.find((target) => target.id === currentProjectId.value)?.label ?? props.session.projectName ?? null);

const FORK_HINT = "A new session with a copy of this conversation.";

/** The menu's footer while no row is highlighted: where the session lives and what runs it. */
const menuFooter = computed(() => {
  const harness = harnesses.value.find((item) => item.type === props.session.harnessType)?.displayName;
  return [props.session.projectName ?? props.session.workspaceDisplayName, props.session.branch, harness]
    .filter(Boolean)
    .join(" · ");
});


function handleSelect(event: MouseEvent): void {
  if (isInlineEditing.value) {
    return;
  }

  if (readOnly.value) {
    emit("select", props.session);
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
  if (event.key === "F2" && !readOnly.value) {
    event.preventDefault();
    startRename();
    return;
  }
  // Alt+↑/↓ moves a pinned session up or down in the Pinned group.
  if (event.altKey && pinned.value && (event.key === "ArrowUp" || event.key === "ArrowDown")) {
    event.preventDefault();
    void handleMovePin(event.key === "ArrowUp" ? -1 : 1);
    return;
  }
  // A row with children opens and closes like a tree item.
  if (props.hasChildren && (event.key === "ArrowRight" || event.key === "ArrowLeft")) {
    const open = event.key === "ArrowRight";
    if (open === Boolean(props.childrenExpanded)) return;
    event.preventDefault();
    emit("toggleChildren");
  }
}

const runningChipLabel = computed(() => {
  const count = props.runningCount ?? 0;
  return count === 1 ? "1 thing running in the background" : `${count} things running in the background`;
});

function handleArchive(): void {
  isContextMenuOpen.value = false;
  archiveQueue.archive([sessionId.value]);
}

const pinned = computed(() => isPinned(props.session));
/** Any session in the list can be pinned, except an archived one. */
const canPin = computed(() => !readOnly.value && !isArchivedSession.value);
const canMovePinUp = computed(() => pinned.value && pinnedNeighbourFor(sessionsStore.sessions, sessionId.value, -1) !== undefined);
const canMovePinDown = computed(() => pinned.value && pinnedNeighbourFor(sessionsStore.sessions, sessionId.value, 1) !== undefined);

function handleTogglePin(): void {
  isContextMenuOpen.value = false;
  void (pinned.value ? pins.unpin(sessionId.value) : pins.pin(sessionId.value));
}

async function handleMovePin(delta: -1 | 1): Promise<void> {
  isContextMenuOpen.value = false;
  await pins.move(sessionId.value, delta);
  // The row moved in the list: keep the keyboard on it.
  await nextTick();
  [...document.querySelectorAll<HTMLElement>(".session-item-shell[data-session-id]")]
    .find((shell) => shell.dataset.sessionId === sessionId.value)
    ?.querySelector<HTMLElement>(".session-item")
    ?.focus();
}

function handleMoveOut(): void {
  isContextMenuOpen.value = false;
  void lineageMoves.moveOut(sessionId.value);
}

function handleMoveBack(): void {
  isContextMenuOpen.value = false;
  void lineageMoves.moveBack(sessionId.value);
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
  if (readOnly.value) return;
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

/** A new, empty session in this session's folder, on its harness and profile: what Fork did before it copied. */
async function handleNewSessionInFolder(): Promise<void> {
  try {
    const response = await startSessionInFolderOf(sessionId.value);
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
  if (projectId === currentProjectId.value) {
    return;
  }

  try {
    await moveSession(sessionId.value, projectId);
    const projectName = projectId === null ? null : (projects.value.find((project) => project.id === projectId)?.name ?? null);
    sessionsStore.patchSessionProject(sessionId.value, projectId, projectName);
  } catch {
    // Errors are handled by the mutation composable state.
  }
}

async function handleCopyPath(): Promise<void> {
  try {
    await navigator.clipboard.writeText(props.session.workspaceDirectory);
  } catch {
    // Clipboard failures are non-fatal.
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
    <ContextMenuTrigger
      as-child
      :disabled="!hasMenu"
    >
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
              'session-item--has-action': (canArchive || canRestore || canPin) && !isSelecting,
              'session-item--has-two-actions': canArchive && canPin && !isSelecting,
              [`session-item--dim-${rowDim}`]: rowDim > 0,
            }"
            :aria-current="active ? 'true' : undefined"
            :aria-pressed="isSelecting ? isSelected : undefined"
            :aria-expanded="hasChildren ? Boolean(childrenExpanded) : undefined"
            :title="openOnMachine ? `Open on ${openOnMachine}` : 'Double-click to rename'"
            :data-testid="openOnMachine ? 'machine-session-row' : 'session-row'"
            @click="handleSelect"
            @dblclick="startRename"
            @keydown="handleRowKeydown"
          >
            <span
              v-if="isSelecting"
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

            <span
              v-if="hasChildren"
              class="session-caret"
              :class="{ 'session-caret--open': childrenExpanded }"
              :title="childrenExpanded ? 'Hide what it started' : 'Show what it started'"
              aria-hidden="true"
              data-testid="session-children-toggle"
              @click.stop="emit('toggleChildren')"
            >
              <ChevronRight />
            </span>

            <span class="session-copy">
              <span class="session-title">{{ displayTitle }}</span>
            </span>

            <span
              v-if="runningCount"
              class="session-running-chip"
              :title="runningChipLabel"
              data-testid="session-running-chip"
            >
              <LoaderCircle aria-hidden="true" />
              {{ runningCount }}
              <span class="sr-only">{{ runningChipLabel }}</span>
            </span>

            <PrBadge
              v-if="prBadge"
              v-bind="prBadge"
            />

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
              v-else-if="kindLabel"
              class="session-kind"
              data-testid="session-kind"
            >{{ kindLabel }}</span>
            <span
              v-else-if="rowStatus.label && !showProgressCount"
              class="session-meta"
              :class="`session-meta--${rowStatus.tone}`"
            >{{ rowStatus.label }}</span>
          </button>
          <button
            v-if="canPin && !isSelecting"
            type="button"
            class="session-row-action session-row-action--pin"
            :class="{ 'session-row-action--second': canArchive, 'session-row-action--on': pinned }"
            :aria-label="pinned ? `Unpin ${displayTitle}` : `Pin ${displayTitle}`"
            :title="pinned ? 'Unpin' : 'Pin to top'"
            data-testid="session-row-pin"
            @click.stop="handleTogglePin"
          >
            <PinOff
              v-if="pinned"
              aria-hidden="true"
            />
            <Pin
              v-else
              aria-hidden="true"
            />
          </button>
          <button
            v-if="canRestore && !isSelecting"
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
            v-else-if="canArchive && !isSelecting"
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

    <ContextMenuContent class="w-66">
      <template v-if="readOnly">
        <ContextMenuItem
          :hint="`Copies ${session.workspaceDirectory}. It's a folder on ${openOnMachine}, so it doesn't open here.`"
          data-testid="session-context-copy-path"
          @select="handleCopyPath"
        >
          <Copy class="size-3.5" />
          Copy path
        </ContextMenuItem>
        <ContextMenuItem
          :hint="`Copies ${sessionId}.`"
          @select="handleCopySessionId"
        >
          <Copy class="size-3.5" />
          Copy session ID
        </ContextMenuItem>
      </template>
      <template v-else>
        <!-- Change this session -->
        <ContextMenuItem
          :disabled="isAnyActionPending"
          @select="startRename"
        >
          <Pencil class="size-3.5" />
          Rename
          <ContextMenuShortcut>F2</ContextMenuShortcut>
        </ContextMenuItem>

        <ContextMenuItem
          v-if="canPin"
          :hint="pinned
            ? 'Puts it back in its project, newest first.'
            : 'Keeps it in Pinned, above your projects. Drag pinned sessions to put them in order.'"
          data-testid="session-context-pin"
          @select="handleTogglePin"
        >
          <PinOff
            v-if="pinned"
            class="size-3.5"
          />
          <Pin
            v-else
            class="size-3.5"
          />
          {{ pinned ? "Unpin" : "Pin to top" }}
        </ContextMenuItem>
        <template v-if="pinned">
          <ContextMenuItem
            :disabled="!canMovePinUp"
            hint="Or press Alt+↑ on the row."
            data-testid="session-context-pin-up"
            @select="handleMovePin(-1)"
          >
            <ArrowUp class="size-3.5" />
            Move up
            <ContextMenuShortcut>Alt+↑</ContextMenuShortcut>
          </ContextMenuItem>
          <ContextMenuItem
            :disabled="!canMovePinDown"
            hint="Or press Alt+↓ on the row."
            data-testid="session-context-pin-down"
            @select="handleMovePin(1)"
          >
            <ArrowDown class="size-3.5" />
            Move down
            <ContextMenuShortcut>Alt+↓</ContextMenuShortcut>
          </ContextMenuItem>
        </template>

        <ContextMenuItem
          v-if="canArchive"
          :disabled="isAnyActionPending"
          hint="Hides it from the list. You can undo it for a few seconds, then find it under Archived."
          data-testid="session-context-archive"
          @select="handleArchive"
        >
          <Archive class="size-3.5" />
          Archive
        </ContextMenuItem>

        <ContextMenuItem
          v-if="canRestore"
          :disabled="isAnyActionPending"
          hint="Puts it back in the list."
          data-testid="session-context-restore"
          @select="handleRestore"
        >
          <ArchiveRestore class="size-3.5" />
          Restore
        </ContextMenuItem>

        <!-- Start from it -->
        <template v-if="!isArchivedSession">
          <ContextMenuSeparator />

          <ContextMenuItem
            v-if="showFork"
            :disabled="isAnyActionPending || !canFork"
            :hint="FORK_HINT"
            data-testid="session-context-fork"
            @select="handleFork"
          >
            <GitFork class="size-3.5" />
            <!-- A row that's off can't be highlighted, so it says why on the row instead of in the footer. -->
            <span
              v-if="forkDisabledReason"
              class="flex min-w-0 flex-col"
            >
              <span>Fork</span>
              <span
                class="text-[11.5px] text-muted"
                data-testid="session-context-fork-note"
              >{{ forkDisabledReason }}</span>
            </span>
            <template v-else>
              Fork
            </template>
          </ContextMenuItem>

          <ContextMenuItem
            :disabled="isAnyActionPending"
            hint="Same folder and harness, an empty conversation."
            data-testid="session-context-new-in-folder"
            @select="handleNewSessionInFolder"
          >
            <Plus class="size-3.5" />
            New session in this folder
          </ContextMenuItem>
        </template>

        <!-- Reuse it -->
        <ContextMenuSeparator />

        <ContextMenuItem
          :disabled="isAnyActionPending"
          hint="Opens Automations with this session's first message and folder filled in."
          data-testid="session-repeat-on-schedule"
          @select="handleRepeatOnSchedule"
        >
          <Repeat class="size-3.5" />
          Repeat on a schedule…
        </ContextMenuItem>

        <ContextMenuItem
          v-if="canSaveAsWorkflow"
          :disabled="isAnyActionPending"
          :hint="DRAFT_COST_NOTE"
          data-testid="session-save-as-workflow"
          @select="handleSaveAsWorkflow"
        >
          <Sparkles class="size-3.5" />
          Save as workflow…
        </ContextMenuItem>

        <!-- Take it elsewhere -->
        <ContextMenuSeparator />

        <OpenToolContextSubmenu :directory="session.workspaceDirectory" />

        <ContextMenuSub>
          <ContextMenuSubTrigger
            :disabled="isAnyActionPending"
            :hint="currentProjectLabel ? `Now in ${currentProjectLabel}.` : undefined"
          >
            <FolderOpen class="size-3.5" />
            Move to project
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
                @select="handleMove(project.id)"
              >
                <span class="min-w-0 flex-1 truncate">{{ project.label }}</span>
                <Check
                  v-if="project.id === currentProjectId"
                  class="size-3.5 text-accent"
                  aria-label="Current project"
                />
              </ContextMenuItem>
            </template>
          </ContextMenuSubContent>
        </ContextMenuSub>

        <ContextMenuItem
          v-if="moveOutLabel"
          :disabled="isAnyActionPending"
          hint="Lists it on its own instead of under the session it came from."
          data-testid="session-context-move-out"
          @select="handleMoveOut"
        >
          <CornerLeftUp class="size-3.5" />
          <span class="truncate">{{ moveOutLabel }}</span>
        </ContextMenuItem>

        <ContextMenuItem
          v-if="moveBackLabel"
          :disabled="isAnyActionPending"
          hint="Lists it under the session it came from again."
          data-testid="session-context-move-back"
          @select="handleMoveBack"
        >
          <CornerDownRight class="size-3.5" />
          <span class="truncate">{{ moveBackLabel }}</span>
        </ContextMenuItem>

        <ContextMenuItem
          :disabled="isAnyActionPending"
          :hint="`Copies ${sessionId}.`"
          @select="handleCopySessionId"
        >
          <Copy class="size-3.5" />
          Copy session ID
        </ContextMenuItem>

        <template v-if="canDelete">
          <ContextMenuSeparator />

          <ContextMenuItem
            variant="destructive"
            :disabled="isAnyActionPending"
            hint="Deletes the session and its history. Fleet asks first; it can't be undone."
            @select="openDeleteDialog"
          >
            <Trash2 class="size-3.5" />
            Delete permanently…
          </ContextMenuItem>
        </template>
      </template>

      <ContextMenuHint v-if="!readOnly">
        {{ menuFooter }}
      </ContextMenuHint>
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

/* Pin sits left of Archive; it stays lit on a pinned row. */
.session-row-action--second {
  right: 31px;
}

.session-row-action--on {
  color: var(--accent);
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
.session-item-shell:hover .session-item--has-two-actions .pr-badge,
.session-item-shell:hover .session-item--has-two-actions .session-running-chip,
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

/* Under its parent: how the session came from it. */
.session-kind {
  flex-shrink: 0;
  color: var(--muted);
  font-size: 10.5px;
  font-weight: 600;
  letter-spacing: 0.04em;
  text-transform: uppercase;
}

/* What the session's agent has running: subagents, background shells, monitors. */
.session-running-chip {
  display: inline-flex;
  flex-shrink: 0;
  align-items: center;
  gap: 3px;
  height: 18px;
  padding: 0 6px;
  border-radius: 999px;
  background: color-mix(in srgb, var(--running) 14%, transparent);
  color: var(--running);
  font-size: 11px;
  font-weight: 600;
  font-variant-numeric: tabular-nums;
}

.session-running-chip svg {
  width: 11px;
  height: 11px;
  stroke-width: 2.25;
}

.session-caret {
  display: inline-grid;
  flex-shrink: 0;
  place-items: center;
  width: 14px;
  height: 14px;
  margin-left: -4px;
  border-radius: 4px;
  color: var(--muted);
}

.session-caret:hover {
  color: var(--text);
}

.session-caret svg {
  width: 14px;
  height: 14px;
  transition: transform var(--transition);
}

.session-caret--open svg {
  transform: rotate(90deg);
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

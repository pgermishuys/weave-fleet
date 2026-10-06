<script setup lang="ts">
import { computed, shallowRef } from "vue";
import { ArrowDown, ArrowUp, ChevronDown, Pencil, Plus, Trash2 } from "lucide-vue-next";
import {
  ContextMenu,
  ContextMenuContent,
  ContextMenuItem,
  ContextMenuSeparator,
  ContextMenuShortcut,
  ContextMenuTrigger,
} from "@/components/ui/context-menu";
import {
  useDeleteProject,
  useReorderProject,
  useUpdateProject,
  type DeleteProjectMode,
} from "@/composables/use-session-actions";
import type { SessionListItem } from "@/api/client";
import type { NewSessionDraftRow } from "@/stores/workspace-ui";
import ConfirmDeleteProjectDialog from "./ConfirmDeleteProjectDialog.vue";
import DraftSessionRow from "./DraftSessionRow.vue";
import InlineEdit from "./InlineEdit.vue";
import SessionItem from "./SessionItem.vue";
import SubagentSessionRow from "./SubagentSessionRow.vue";
import WorkflowRunGroup from "@/components/workflows/WorkflowRunGroup.vue";
import { groupRunSessions } from "@/lib/workflows";
import { useWorkflowsStore } from "@/stores/workflows";
import { heightEnter, heightLeave } from "@/lib/height-transition";
import type { RunningWorkItem } from "@/lib/running-work";
import { lineageDescendants, lineageKindLabel, movableOutOf, nestLineage, sessionAgentState } from "@/lib/session-lineage";

interface ProjectGroupModel {
  id: string;
  projectId: string | null;
  name: string;
  isUngrouped: boolean;
  canMoveUp: boolean;
  canMoveDown: boolean;
  moveUpTargets: Array<{ projectId: string; position: number }>;
  moveDownTargets: Array<{ projectId: string; position: number }>;
  sessionCount: number;
  sessions: SessionListItem[];
}

interface Props {
  project: ProjectGroupModel;
  expanded: boolean;
  activeSessionId: string | null;
  activeDragSessionId: string | null;
  activeDragProjectId: string | null;
  /** The New Session page's draft, when it will land in this group. */
  draft?: NewSessionDraftRow | null;
  draftActive?: boolean;
  /** Session id → row key, for sessions that took over the draft row. */
  rowKeys?: Readonly<Record<string, string>>;
  /** Session id → how much work its agent has running: the chip on its row. */
  runningCounts?: ReadonlyMap<string, number>;
  /** Session id → its running subagents that have a session of their own, nested under it. */
  runningSubagents?: ReadonlyMap<string, readonly RunningWorkItem[]>;
}

interface Emits {
  toggle: [projectId: string];
  selectSession: [session: SessionListItem];
  newSession: [projectId: string];
  projectChanged: [];
  sessionChanged: [];
  moveSession: [sessionId: string, targetProjectId: string | null];
  /** A fork or a started session dragged out of its parent, onto its project: it stands on its own. */
  moveOutOfParent: [sessionId: string];
  dragSessionStart: [sessionId: string, projectId: string | null];
  dragSessionEnd: [];
  openDraft: [];
}

const props = defineProps<Props>();
const emit = defineEmits<Emits>();

const workflows = useWorkflowsStore();

/** Forks and sessions another session started nest under it; running subagents join them from the running work. */
const lineage = computed(() => nestLineage(props.project.sessions));

/** A workflow run's step sessions group under one row; everything else is a row of its own. */
const entries = computed(() => groupRunSessions(lineage.value.roots, workflows.runForSession));

type ChildRow =
  | { kind: "subagent"; key: string; work: RunningWorkItem }
  | { kind: "session"; key: string; item: SessionListItem; label: string };

/**
 * A session's running subagents, each followed by the subagents it runs itself (Claude Code's nested subagents run in
 * the subagent's own session).
 */
function subagentRowsOf(sessionId: string, seen = new Set<string>([sessionId])): ChildRow[] {
  return (props.runningSubagents?.get(sessionId) ?? []).flatMap((work): ChildRow[] => {
    const child = work.childSessionId;
    const nested = child && !seen.has(child) ? subagentRowsOf(child, seen.add(child)) : [];
    return [{ kind: "subagent", key: `work:${work.id}`, work }, ...nested];
  });
}

/**
 * What shows under a top-level session: everything that came from it, however deep, one indent in and in tree order.
 * Its running subagents first, then each session it forked or started followed by that one's own subagents and
 * children. The kind label says how each came; the session's own header names its exact parent.
 */
function childRowsOf(session: SessionListItem): ChildRow[] {
  return [
    ...subagentRowsOf(session.session.id),
    ...lineageDescendants(session, lineage.value.childrenOf).flatMap(({ item, kind }): ChildRow[] => [
      { kind: "session", key: rowKey(item), item, label: lineageKindLabel(kind) },
      ...subagentRowsOf(item.session.id),
    ]),
  ];
}

function hasChildren(session: SessionListItem): boolean {
  return childRowsOf(session).length > 0;
}

/** Children the user opened or closed, by parent id; the rest follow {@link opensByItself}. */
const childrenOverride = shallowRef<Record<string, boolean>>({});

/** A parent shows its children while it or one of them is open, or while one of them works or waits on you. */
function opensByItself(session: SessionListItem): boolean {
  if (session.session.id === props.activeSessionId) return true;
  return childRowsOf(session).some((row) => row.kind === "subagent"
    || row.item.session.id === props.activeSessionId
    || ["running", "waiting"].includes(sessionAgentState(row.item)));
}

function childrenExpanded(session: SessionListItem): boolean {
  return childrenOverride.value[session.session.id] ?? opensByItself(session);
}

function toggleChildren(session: SessionListItem): void {
  childrenOverride.value = { ...childrenOverride.value, [session.session.id]: !childrenExpanded(session) };
}

const isContextMenuOpen = shallowRef(false);
const isInlineEditing = shallowRef(false);
const isDeleteDialogOpen = shallowRef(false);

const {
  updateProject,
  isUpdating,
} = useUpdateProject();
const {
  reorderProject,
  isReordering,
} = useReorderProject();
const {
  deleteProject,
  isDeleting,
} = useDeleteProject();

const canShowContextMenu = computed(() => !props.project.isUngrouped && props.project.projectId !== null);
const isAnyActionPending = computed(() => isUpdating.value || isReordering.value || isDeleting.value);

// Drag-and-drop drop target state
const dragEnterCount = shallowRef(0);

/** The nested fork or started session being dragged, when it's one of this project's: dropping it here moves it out. */
const draggedOutOfParent = computed(() => {
  const id = props.activeDragSessionId;
  if (!id || props.activeDragProjectId !== props.project.projectId) return null;
  const item = props.project.sessions.find((candidate) => candidate.session.id === id);
  return item && movableOutOf(item) && !lineage.value.roots.includes(item) ? item : null;
});

/** Whether the dragged session would land somewhere new: another project's session, or one moving out of its parent. */
const acceptsDrag = computed(() => Boolean(props.activeDragSessionId)
  && (props.activeDragProjectId !== props.project.projectId || draggedOutOfParent.value !== null));

/**
 * Whether the drag is over the family the dragged session is already in (its top-level session and everything under
 * it): dropping there does nothing, since that's where it is.
 */
function overOwnFamily(event: DragEvent): boolean {
  const dragged = draggedOutOfParent.value;
  if (!dragged) return false;
  const rootId = (event.target as Element | null)?.closest?.("[data-family]")?.getAttribute("data-family");
  const root = rootId ? props.project.sessions.find((item) => item.session.id === rootId) : null;
  return Boolean(root) && childRowsOf(root!).some((row) => row.kind === "session" && row.item.session.id === dragged.session.id);
}

const isOverOwnFamily = shallowRef(false);
const isDropTarget = computed(() => dragEnterCount.value > 0 && acceptsDrag.value && !isOverOwnFamily.value);

function handleSessionDragStart(sessionId: string, projectId: string | null): void {
  emit("dragSessionStart", sessionId, projectId);
}

function handleSessionDragEnd(): void {
  emit("dragSessionEnd");
}

function handleDragOver(event: DragEvent): void {
  isOverOwnFamily.value = overOwnFamily(event);
  // Must prevent default to allow drop
  if (acceptsDrag.value && !isOverOwnFamily.value) {
    event.preventDefault();
    if (event.dataTransfer) {
      event.dataTransfer.dropEffect = "move";
    }
  }
}

function handleDragEnter(event: DragEvent): void {
  if (props.activeDragSessionId) {
    dragEnterCount.value++;
    isOverOwnFamily.value = overOwnFamily(event);
  }
}

function handleDragLeave(): void {
  if (props.activeDragSessionId) {
    dragEnterCount.value = Math.max(0, dragEnterCount.value - 1);
  }
}

function handleDrop(event: DragEvent): void {
  dragEnterCount.value = 0;
  isOverOwnFamily.value = false;

  if (!props.activeDragSessionId) {
    return;
  }

  // A fork or a started session dropped on its own project, outside its family, moves out of its parent.
  const movingOut = draggedOutOfParent.value;
  if (movingOut) {
    event.preventDefault();
    if (!overOwnFamily(event)) emit("moveOutOfParent", movingOut.session.id);
    return;
  }

  event.preventDefault();

  const sessionId = props.activeDragSessionId;
  const sourceProjectId = props.activeDragProjectId;

  if (!sessionId) {
    return;
  }

  const targetProjectId = props.project.projectId;

  // No-op: same project
  if (sourceProjectId === targetProjectId) {
    return;
  }

  emit("moveSession", sessionId, targetProjectId);

  // Auto-expand the target project if it is currently collapsed
  if (!props.expanded) {
    emit("toggle", props.project.id);
  }
}

/** A session that started from the draft keeps the draft row's key, so it replaces it in place. */
function rowKey(session: SessionListItem): string {
  return props.rowKeys?.[session.session.id] ?? session.session.id;
}

function handleToggle(): void {
  emit("toggle", props.project.id);
}

function handleSessionSelect(session: SessionListItem): void {
  emit("selectSession", session);
}

function handleContextMenuOpenChange(value: boolean): void {
  isContextMenuOpen.value = value;
}

function startRename(): void {
  if (!canShowContextMenu.value || isAnyActionPending.value) {
    return;
  }

  isContextMenuOpen.value = false;
  isInlineEditing.value = true;
}

function cancelRename(): void {
  isInlineEditing.value = false;
}

function handleHeaderKeydown(event: KeyboardEvent): void {
  if (event.key !== "F2") {
    return;
  }

  event.preventDefault();
  startRename();
}

function handleNewSessionRequest(): void {
  if (!props.project.projectId) {
    return;
  }

  isContextMenuOpen.value = false;
  emit("newSession", props.project.projectId);
}

async function handleRename(nextName: string): Promise<void> {
  const trimmedName = nextName.trim();
  isInlineEditing.value = false;

  if (!props.project.projectId || trimmedName.length === 0 || trimmedName === props.project.name) {
    return;
  }

  try {
    await updateProject(props.project.projectId, { name: trimmedName, description: null });
    emit("projectChanged");
  } catch {
    // Errors are handled by the mutation composable state.
  }
}

async function handleMoveUp(): Promise<void> {
  if (!props.project.projectId || props.project.moveUpTargets.length === 0) {
    return;
  }

  isContextMenuOpen.value = false;

  try {
    for (const target of props.project.moveUpTargets) {
      await reorderProject(target.projectId, target.position);
    }

    emit("projectChanged");
  } catch {
    // Errors are handled by the mutation composable state.
  }
}

async function handleMoveDown(): Promise<void> {
  if (!props.project.projectId || props.project.moveDownTargets.length === 0) {
    return;
  }

  isContextMenuOpen.value = false;

  try {
    for (const target of props.project.moveDownTargets) {
      await reorderProject(target.projectId, target.position);
    }

    emit("projectChanged");
  } catch {
    // Errors are handled by the mutation composable state.
  }
}

function openDeleteDialog(): void {
  if (!canShowContextMenu.value) {
    return;
  }

  isContextMenuOpen.value = false;
  isDeleteDialogOpen.value = true;
}

async function handleDelete(mode: DeleteProjectMode): Promise<void> {
  if (!props.project.projectId) {
    return;
  }

  try {
    await deleteProject(props.project.projectId, mode);
    isDeleteDialogOpen.value = false;
    emit("projectChanged");
  } catch {
    // Errors are handled by the mutation composable state.
  }
}
</script>

<template>
  <section
    class="project-group"
    :data-project-id="project.projectId"
    @dragover="handleDragOver"
    @dragenter="handleDragEnter"
    @dragleave="handleDragLeave"
    @drop="handleDrop"
  >
    <ContextMenu
      v-if="canShowContextMenu && !isInlineEditing"
      :open="isContextMenuOpen"
      @update:open="handleContextMenuOpenChange"
    >
      <ContextMenuTrigger as-child>
        <div class="project-shell">
          <button
            type="button"
            class="project-header"
            :class="{ collapsed: !expanded, 'project-header--drop-target': isDropTarget }"
            :aria-expanded="expanded"
            :aria-dropeffect="isDropTarget ? 'move' : 'none'"
            @click="handleToggle"
            @keydown="handleHeaderKeydown"
          >
            <ChevronDown
              class="project-chevron"
              aria-hidden="true"
            />
            <span class="project-copy">
              <span class="project-title">{{ project.name }}</span>
            </span>

            <span class="project-count">{{ project.sessionCount }}</span>
          </button>
        </div>
      </ContextMenuTrigger>

      <ContextMenuContent class="w-56">
        <ContextMenuItem
          :disabled="isAnyActionPending"
          @select="handleNewSessionRequest"
        >
          <Plus class="size-3.5" />
          New session
        </ContextMenuItem>

        <ContextMenuSeparator />

        <ContextMenuItem
          :disabled="isAnyActionPending"
          @select="startRename"
        >
          <Pencil class="size-3.5" />
          Rename
          <ContextMenuShortcut>F2</ContextMenuShortcut>
        </ContextMenuItem>

        <ContextMenuItem
          v-if="project.canMoveUp"
          :disabled="isAnyActionPending"
          @select="handleMoveUp"
        >
          <ArrowUp class="size-3.5" />
          Move up
        </ContextMenuItem>

        <ContextMenuItem
          v-if="project.canMoveDown"
          :disabled="isAnyActionPending"
          @select="handleMoveDown"
        >
          <ArrowDown class="size-3.5" />
          Move down
        </ContextMenuItem>

        <ContextMenuSeparator />

        <ContextMenuItem
          variant="destructive"
          :disabled="isAnyActionPending"
          @select="openDeleteDialog"
        >
          <Trash2 class="size-3.5" />
          Delete…
        </ContextMenuItem>
      </ContextMenuContent>
    </ContextMenu>

    <div
      v-else
      class="project-shell"
    >
      <button
        v-if="!isInlineEditing"
        type="button"
        class="project-header"
        :class="{ collapsed: !expanded, 'project-header--drop-target': isDropTarget }"
        :aria-expanded="expanded"
        :aria-dropeffect="isDropTarget ? 'move' : 'none'"
        @click="handleToggle"
        @keydown="handleHeaderKeydown"
      >
        <ChevronDown
          class="project-chevron"
          aria-hidden="true"
        />
        <span class="project-copy">
          <span class="project-title">{{ project.name }}</span>
        </span>

        <span class="project-count">{{ project.sessionCount }}</span>
      </button>

      <div
        v-else
        class="project-header project-header--editing"
        :class="{ collapsed: !expanded }"
      >
        <ChevronDown
          class="project-chevron"
          aria-hidden="true"
        />
        <span class="project-copy">
          <InlineEdit
            :initial-value="project.name"
            :disabled="isUpdating"
            placeholder="Project name"
            @cancel="cancelRename"
            @commit="handleRename"
          />
        </span>

        <span class="project-spacer" />
      </div>
    </div>

    <Transition
      :css="false"
      @enter="heightEnter"
      @leave="heightLeave"
    >
      <TransitionGroup
        v-if="expanded"
        tag="div"
        class="project-content"
        :css="false"
        @enter="heightEnter"
        @leave="heightLeave"
      >
        <div
          v-if="draft"
          :key="draft.key"
          class="project-row"
        >
          <DraftSessionRow
            :draft="draft"
            :active="draftActive ?? false"
            @open="emit('openDraft')"
          />
        </div>
        <!-- SessionItem renders several root nodes (row + dialogs), so each row
             gets a single element wrapper the transition can animate. -->
        <div
          v-for="entry in entries"
          :key="entry.kind === 'run' ? `run:${entry.runId}` : rowKey(entry.session)"
          class="project-row"
          :data-family="entry.kind === 'run' ? undefined : entry.session.session.id"
        >
          <WorkflowRunGroup
            v-if="entry.kind === 'run'"
            :run="entry.run"
            :steps="entry.steps"
            :active-session-id="activeSessionId"
            @select-session="handleSessionSelect"
            @drag-session-start="handleSessionDragStart"
            @drag-session-end="handleSessionDragEnd"
          />
          <template v-else>
            <SessionItem
              :session="entry.session"
              :active="entry.session.session.id === activeSessionId"
              :running-count="runningCounts?.get(entry.session.session.id)"
              :has-children="hasChildren(entry.session)"
              :children-expanded="childrenExpanded(entry.session)"
              @select="handleSessionSelect"
              @toggle-children="toggleChildren(entry.session)"
              @drag-session-start="handleSessionDragStart"
              @drag-session-end="handleSessionDragEnd"
            />
            <div
              v-if="hasChildren(entry.session) && childrenExpanded(entry.session)"
              class="session-children"
              role="group"
              :aria-label="`Started from ${entry.session.session.title || 'this session'}`"
              data-testid="session-children"
            >
              <div
                v-for="child in childRowsOf(entry.session)"
                :key="child.key"
                class="session-child"
              >
                <SubagentSessionRow
                  v-if="child.kind === 'subagent'"
                  :item="child.work"
                  :active="child.work.childSessionId === activeSessionId"
                />
                <SessionItem
                  v-else
                  :session="child.item"
                  :kind-label="child.label"
                  :active="child.item.session.id === activeSessionId"
                  :running-count="runningCounts?.get(child.item.session.id)"
                  @select="handleSessionSelect"
                  @drag-session-start="handleSessionDragStart"
                  @drag-session-end="handleSessionDragEnd"
                />
              </div>
            </div>
          </template>
        </div>
      </TransitionGroup>
    </Transition>

    <ConfirmDeleteProjectDialog
      v-model:open="isDeleteDialogOpen"
      :project-name="project.name"
      :session-count="project.sessionCount"
      :is-deleting="isDeleting"
      @confirm="handleDelete"
    />
  </section>
</template>

<style scoped>
.project-group + .project-group {
  margin-top: 10px;
}

.project-shell {
  width: 100%;
}

.project-header {
  width: 100%;
  min-height: 26px;
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 0 10px 0 6px;
  cursor: pointer;
  border: 0;
  border-radius: var(--radius-btn);
  background: transparent;
  color: var(--muted);
  text-align: left;
  transition: background var(--transition), color var(--transition);
}

.project-header:hover {
  background: color-mix(in srgb, var(--text) 5%, transparent);
  color: var(--text);
}

.project-header--editing {
  cursor: default;
}

.project-header--editing:hover {
  background: transparent;
}

.project-header--drop-target {
  outline: 2px dashed var(--accent);
  outline-offset: -2px;
  background: rgba(var(--accent-rgb, 139, 92, 246), 0.08);
}

.project-header:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
}

.project-chevron {
  width: 13px;
  height: 13px;
  flex-shrink: 0;
  color: color-mix(in srgb, var(--muted) 70%, transparent);
  transition: transform var(--transition);
}

.project-header.collapsed .project-chevron {
  transform: rotate(-90deg);
}

.project-copy {
  flex: 1 1 auto;
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.project-title {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 12px;
  font-weight: 600;
  line-height: 1.3;
}

.project-count {
  flex-shrink: 0;
  font-size: 12px;
  font-weight: 500;
  color: color-mix(in srgb, var(--muted) 70%, transparent);
  font-variant-numeric: tabular-nums;
}

.project-spacer {
  flex: 1;
}

/* What a session started, under it: running subagents, forks, sessions its agent started. */
.session-children {
  display: flex;
  flex-direction: column;
  gap: 1px;
  margin: 1px 0 2px 13px;
  padding-left: 10px;
  border-left: 1px solid var(--border);
}

.session-children :deep(.session-item) {
  min-height: 28px;
}

.project-content {
  padding-top: 2px;
  overflow: hidden;
}
</style>

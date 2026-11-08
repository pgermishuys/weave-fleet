<script setup lang="ts">
import { computed, onUnmounted, shallowRef, watch } from "vue";
import type { SessionListItem } from "@/api/client";
import MachineHeader from "@/components/sessions/MachineHeader.vue";
import ProjectGroup from "@/components/sessions/ProjectGroup.vue";
import { isPinned, splitPinned } from "@/lib/session-pins";
import {
  PINNED_GROUP_ID,
  buildProjectGroups,
  draftGroupIdFor,
  draftGroupKeyFor,
  filterProjectGroups,
  pinnedProjectGroup,
  sessionMatchesQuery,
} from "@/lib/session-project-groups";
import { useMoveSession } from "@/composables/use-session-actions";
import { provideMachineTarget } from "@/lib/machine-target";
import { useArchiveQueueStore } from "@/stores/archive-queue";
import { useLineageMovesStore } from "@/stores/lineage-moves";
import { useMachinesStore, type MachineEntry, type MachineSessions } from "@/stores/machines";
import { useSessionPinsStore } from "@/stores/session-pins";
import { useSessionSelectionStore } from "@/stores/session-selection";
import type { NewSessionDraftRow } from "@/stores/workspace-ui";
import { machineGroupKey, projectGroupKey, useSidebarStore } from "@/stores/sidebar";

/**
 * A machine that didn't serve the page: its sessions as it last listed them, refreshed on a timer, in the same tree the
 * live machine shows (Pinned, its projects in their order, what each session started under it). Its rows open the
 * session in place and do what the live machine's rows do (rename, pin, archive, move, drag within this tree, pick for
 * a bulk archive), and its projects have the live machine's + and menu (new session, rename, move, delete). Everything
 * in the group asks this machine, so nothing acts on the wrong one, and its list is read again after each change. While
 * it isn't answering, its rows only open.
 */
const props = defineProps<{
  machine: MachineEntry;
  state: MachineSessions | undefined;
  /** Lower-case filter from the sidebar's search box. */
  query: string;
  /** The new-session draft, when it starts on this machine. */
  draft?: NewSessionDraftRow | null;
  draftActive?: boolean;
  /** The open session, when it's on this machine: its row is marked open. */
  activeSessionId?: string | null;
}>();

const emit = defineEmits<{
  open: [session: SessionListItem];
  openDraft: [];
  newSession: [];
  /** The + (or New session in the menu) on one of its projects: a new session on this machine, in that project. */
  newSessionIn: [projectId: string];
  workHere: [];
}>();

const sidebar = useSidebarStore();
const expanded = computed(() => !sidebar.isGroupCollapsed(machineGroupKey(props.machine.key)));

const projects = computed(() => props.state?.projects ?? []);
const projectsById = computed(() => new Map(projects.value.map((project) => [project.id, project])));

const archiveQueue = useArchiveQueueStore();
// Waiting on Undo: gone from the list, not yet archived on the machine.
const sessions = computed(() => (props.state?.sessions ?? [])
  .filter((item) => !item.parentSessionId && item.retentionStatus !== "archived" && !archiveQueue.pendingIds.has(item.session.id)));

const split = computed(() => splitPinned(sessions.value));

function matches(item: SessionListItem): boolean {
  return sessionMatchesQuery(item, props.query, projectsById.value);
}

const draftKey = computed(() => (props.draft ? draftGroupKeyFor(props.draft.projectId, projects.value) : null));
const draftGroupId = computed(() => draftGroupIdFor(draftKey.value, projects.value));

const pinnedGroup = computed(() => pinnedProjectGroup(props.query ? split.value.pinned.filter(matches) : split.value.pinned));
/** Pinned shows while it has sessions, and (empty, to drop one in) once a drag here is under way. */
const showPinnedGroup = computed(() => pinnedGroup.value.sessionCount > 0
  || (drag.value !== null && dragUnderway.value && !props.query));

const projectGroups = computed(() => {
  const groups = buildProjectGroups(split.value.rest, projects.value, draftKey.value);
  return props.query ? filterProjectGroups(groups, props.query, matches) : groups;
});

const count = computed(() => pinnedGroup.value.sessionCount
  + projectGroups.value.reduce((total, group) => total + group.sessionCount, 0));

/** What each session's agent had running when the list came back: the chip on its row. */
const runningCounts = computed(() => new Map(sessions.value
  .filter((item) => item.runningWorkCount)
  .map((item) => [item.session.id, item.runningWorkCount!])));

function isProjectExpanded(groupId: string): boolean {
  return !sidebar.isGroupCollapsed(projectGroupKey(props.machine.key, groupId));
}

function toggleProject(groupId: string): void {
  sidebar.toggleGroupCollapsed(projectGroupKey(props.machine.key, groupId));
}

// A draft shows in its group, so that group opens when a draft lands in it.
watch(draftGroupId, (groupId) => {
  if (groupId) sidebar.setGroupCollapsed(projectGroupKey(props.machine.key, groupId), false);
});

const machines = useMachinesStore();
// The projects' menus (rename, move, delete) and the rows (rename, pin, archive, move) ask this machine. SessionsPanel
// keys the group by machine.
const target = () => machines.targetOfKey(props.machine.key);
provideMachineTarget(target);

const pins = useSessionPinsStore();
const lineageMoves = useLineageMovesStore();
// This component provides the machine, so it can't inject it: the move is given it.
const { moveSession } = useMoveSession(target());

/** The session being dragged in this machine's tree, from where. A drag never leaves the machine it started on. */
const drag = shallowRef<{ sessionId: string; projectId: string | null } | null>(null);
/** A moment after dragstart (see SessionsPanel: Chrome cancels a drag whose row moves during dragstart). */
const dragUnderway = shallowRef(false);
let dragUnderwayTimer: ReturnType<typeof setTimeout> | undefined;
onUnmounted(() => clearTimeout(dragUnderwayTimer));

const dragPinned = computed(() => {
  const id = drag.value?.sessionId;
  const item = id ? sessions.value.find((candidate) => candidate.session.id === id) : undefined;
  return item ? isPinned(item) : false;
});

function handleDragStart(sessionId: string, projectId: string | null): void {
  drag.value = { sessionId, projectId };
  dragUnderway.value = false;
  clearTimeout(dragUnderwayTimer);
  dragUnderwayTimer = setTimeout(() => {
    dragUnderway.value = true;
  });
}

function handleDragEnd(): void {
  drag.value = null;
  dragUnderway.value = false;
  clearTimeout(dragUnderwayTimer);
}

function handlePin(sessionId: string, beforeSessionId: string | null): void {
  drag.value = null;
  void pins.pin(sessionId, beforeSessionId, target());
}

function handleUnpin(sessionId: string): void {
  drag.value = null;
  void pins.unpin(sessionId, target());
}

function handleMoveOutOfParent(sessionId: string): void {
  drag.value = null;
  if (props.query) return;
  void lineageMoves.moveOut(sessionId, target());
}

/** A session dropped on another of this machine's projects: it moves there at once, and the machine is told. */
async function handleMove(sessionId: string, projectId: string | null): Promise<void> {
  const dragged = drag.value;
  drag.value = null;
  // Not while searching, where the groups show only what matches.
  if (props.query || dragged?.sessionId !== sessionId) return;
  if (projectId !== null && !projectsById.value.has(projectId)) return;
  const item = sessions.value.find((candidate) => candidate.session.id === sessionId);
  if (!item) return;
  const previous = { projectId: item.projectId ?? null, projectName: item.projectName ?? null };
  const projectName = projectId === null ? null : projectsById.value.get(projectId)?.name ?? null;
  machines.patchSession(props.machine.key, sessionId, { projectId, projectName });
  try {
    await moveSession(sessionId, projectId);
  } catch {
    machines.patchSession(props.machine.key, sessionId, previous);
  }
  refreshList();
}

// Rows are picked within this machine's tree (a pick never spans machines); Shift-click takes its range from here.
const selection = useSessionSelectionStore();
watch(
  () => (expanded.value ? [...(showPinnedGroup.value ? [pinnedGroup.value] : []), ...projectGroups.value] : [])
    .filter((group) => isProjectExpanded(group.id))
    .flatMap((group) => group.sessions.map((session) => session.session.id)),
  (order) => selection.setVisibleOrder(order, props.machine.key),
  { immediate: true },
);
onUnmounted(() => selection.setVisibleOrder([], props.machine.key));

/** A project was renamed, moved or deleted on this machine: its list comes back at once, not on the next poll. */
function refreshList(): void {
  void machines.refreshMachine(props.machine.key);
}

const unreachable = computed(() => Boolean(props.state?.error));

const note = computed(() => {
  if (props.state?.error) return props.state.loadedAt ? "unreachable · cached" : "unreachable";
  if (!props.state?.loadedAt) return props.state?.loading ? "…" : null;
  return null;
});

/** Why the machine isn't answering: this browser's own check when it has run, else home's last look at it. */
const why = computed(() => (unreachable.value ? machines.diagnoses[props.machine.key] ?? props.machine.diagnosis ?? null : null));
</script>

<template>
  <section
    class="machine-group"
    :class="{ 'machine-group--stale': unreachable }"
    :aria-label="`Sessions on ${machine.name}`"
    data-testid="machine-group"
    :data-machine="machine.key"
  >
    <MachineHeader
      :name="machine.name"
      :unreachable="unreachable"
      :note="note"
      :why="why"
      :count="count"
      :expanded="expanded"
      menu
      @toggle="sidebar.toggleGroupCollapsed(machineGroupKey(machine.key))"
      @new-session="emit('newSession')"
      @work-here="emit('workHere')"
    />

    <template v-if="expanded">
      <p
        v-if="state?.error"
        class="machine-group__error"
        role="status"
      >
        {{ state.error }}
      </p>
      <ProjectGroup
        v-if="showPinnedGroup"
        key="pinned"
        :project="pinnedGroup"
        pinned
        :expanded="isProjectExpanded(PINNED_GROUP_ID)"
        :active-session-id="activeSessionId ?? null"
        :active-drag-session-id="drag?.sessionId ?? null"
        :active-drag-project-id="drag?.projectId ?? null"
        :running-counts="runningCounts"
        :open-on-machine="machine.name"
        :machine-not-answering="!machines.isAnswering(machine.key)"
        data-testid="pinned-group"
        @toggle="toggleProject"
        @select-session="emit('open', $event)"
        @drag-session-start="handleDragStart"
        @drag-session-end="handleDragEnd"
        @pin-session="handlePin"
      />
      <ProjectGroup
        v-for="project in projectGroups"
        :key="project.id"
        :project="project"
        :expanded="isProjectExpanded(project.id)"
        :active-session-id="activeSessionId ?? null"
        :active-drag-session-id="drag?.sessionId ?? null"
        :active-drag-project-id="drag?.projectId ?? null"
        :active-drag-pinned="dragPinned"
        :draft="project.id === draftGroupId ? draft : null"
        :draft-active="draftActive"
        :running-counts="runningCounts"
        :open-on-machine="machine.name"
        :machine-not-answering="!machines.isAnswering(machine.key)"
        @toggle="toggleProject"
        @select-session="emit('open', $event)"
        @open-draft="emit('openDraft')"
        @new-session="emit('newSessionIn', $event)"
        @project-changed="refreshList"
        @drag-session-start="handleDragStart"
        @drag-session-end="handleDragEnd"
        @move-session="handleMove"
        @move-out-of-parent="handleMoveOutOfParent"
        @unpin-session="handleUnpin"
      />
      <p
        v-if="!state?.error && state?.loadedAt && projectGroups.length === 0 && pinnedGroup.sessionCount === 0"
        class="machine-group__empty"
      >
        <template v-if="query">
          No matching sessions
        </template>
        <template v-else>
          No sessions ·
          <button
            type="button"
            class="machine-group__start"
            data-testid="machine-group-new-session"
            @click="emit('newSession')"
          >
            New session
          </button>
        </template>
      </p>
    </template>
  </section>
</template>

<style scoped>
.machine-group__error,
.machine-group__empty {
  margin: 2px 10px 4px 26px;
  font-size: 0.82143rem;
  line-height: 1.4;
  color: var(--muted);
}

.machine-group__start {
  padding: 0;
  border: 0;
  background: none;
  color: var(--coral);
  font: inherit;
  cursor: pointer;
}

.machine-group__start:hover {
  text-decoration: underline;
}

.machine-group__start:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
  border-radius: 2px;
}

.machine-group__error {
  color: color-mix(in srgb, var(--error) 80%, var(--muted));
}

/* An unreachable machine's rows are the last ones it returned. */
.machine-group--stale :deep(.session-item) {
  opacity: 0.6;
}
</style>

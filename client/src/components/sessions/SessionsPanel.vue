<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, shallowRef, useTemplateRef, watch } from "vue";
import { useLocation, useRouter } from "@tanstack/vue-router";
import { Archive, ArchiveRestore, ArrowLeft, FolderPlus, LoaderCircle, Plus, Search, X } from "lucide-vue-next";
import { storeToRefs } from "pinia";
import type { SessionListItem } from "@/api/client";
import { useProjects } from "@/composables/use-projects";
import { useSessions } from "@/composables/use-sessions";
import { useRunningWorkAcrossSessions } from "@/composables/use-running-work";
import { runningSubagentsBySession } from "@/lib/session-lineage";
import { useMoveSession } from "@/composables/use-session-actions";
import { useArchiveQueueStore } from "@/stores/archive-queue";
import { useLineageMovesStore } from "@/stores/lineage-moves";
import { useSessionPinsStore } from "@/stores/session-pins";
import { isPinned, splitPinned } from "@/lib/session-pins";
import {
  PINNED_GROUP_ID,
  buildProjectGroups,
  draftGroupIdFor,
  draftGroupKeyFor,
  filterProjectGroups,
  pinnedProjectGroup,
  sessionMatchesQuery,
  type ProjectTreeGroup,
} from "@/lib/session-project-groups";
import { saveSessionListScroll, takeSessionListScroll } from "@/lib/session-list-scroll";
import { useSessionSelectionStore } from "@/stores/session-selection";
import { useSessionsStore } from "@/stores/sessions";
import { machineGroupKey, projectGroupKey, useSidebarStore } from "@/stores/sidebar";
import { useWorkspaceUiStore } from "@/stores/workspace-ui";
import { useMachinesStore, type MachineEntry } from "@/stores/machines";

import { Button } from "@/components/ui/button";
import MachineHeader from "./MachineHeader.vue";
import MachineSessionsGroup from "./MachineSessionsGroup.vue";
import NewProjectDialog from "./NewProjectDialog.vue";

import ProjectGroup from "./ProjectGroup.vue";

interface ActiveSessionDrag {
  sessionId: string;
  projectId: string | null;
}


const sessionsStore = useSessionsStore();
const archiveQueue = useArchiveQueueStore();
const lineageMoves = useLineageMovesStore();
const pins = useSessionPinsStore();
const selection = useSessionSelectionStore();
const sidebarStore = useSidebarStore();
const workspaceUiStore = useWorkspaceUiStore();
const { newSessionDraftRow, newSessionMachine, sessionRowKeys } = storeToRefs(workspaceUiStore);
const router = useRouter();

let releaseSessionList: (() => void) | null = null;
onMounted(() => {
  releaseSessionList = sidebarStore.registerSessionList();
});
onUnmounted(() => {
  releaseSessionList?.();
  clearTimeout(dragUnderwayTimer);
  selection.clear();
  window.removeEventListener("keydown", handleSelectionKeydown);
});

function handleSelectionKeydown(event: KeyboardEvent): void {
  if (event.key === "Escape" && selection.isSelecting) {
    selection.clear();
  }
}

onMounted(() => {
  window.addEventListener("keydown", handleSelectionKeydown);
});
const pathname = useLocation({
  select: (location) => location.pathname,
});

const { moveSession } = useMoveSession();

const { activeSessionId, retentionStatus } = storeToRefs(sessionsStore);
const {
  isLoading: isSessionsLoading,
  error: sessionsError,
  refetch: refetchSessions,
} = useSessions({ retentionStatus });
const {
  projects,
  isLoading: areProjectsLoading,
  error: projectsError,
  refetch: refetchProjects,
} = useProjects();

const sessions = computed(() => {
  return sessionsStore.sessions.filter((session) => {
    if (session.parentSessionId) {
      return false;
    }

    // Waiting on Undo: gone from the list, not yet archived on the server.
    if (archiveQueue.pendingIds.has(session.session.id)) {
      return false;
    }

    if (retentionStatus.value === "all") {
      return true;
    }

    return session.retentionStatus === retentionStatus.value;
  });
});

const isArchivedView = computed(() => retentionStatus.value === "archived");

/** The session being dragged in the list, from where (app state, not the drag's own data). */
const activeSessionDrag = shallowRef<ActiveSessionDrag | null>(null);

/**
 * Whether the drag has got under way, a moment after dragstart. Chrome cancels a drag whose row moves during
 * dragstart, so the empty Pinned group (which pushes the rows down) waits for this.
 */
const isSessionDragUnderway = shallowRef(false);
let dragUnderwayTimer: ReturnType<typeof setTimeout> | undefined;

/** Pinned sessions (and what came from them) sit in the Pinned group above the projects; the rest in their projects. */
const pinnedSplit = computed(() => isArchivedView.value
  ? { pinned: [] as SessionListItem[], rest: sessions.value }
  : splitPinned(sessions.value));

// What every session's agent has running: a chip on its row, and its running subagents nested under it.
const { running: runningWork, groups: runningWorkGroups } = useRunningWorkAcrossSessions();
const runningCounts = computed(() => new Map(runningWorkGroups.value.map((group) => [group.sessionId, group.items.length])));
const runningSubagents = computed(() => runningSubagentsBySession(runningWork.value));

// Other machines: every machine is listed, in a fixed order (home, then the others as they were added), so switching
// machines only moves the Live tag. The live one is listed in full, the rest as their last polled list.
const machines = useMachinesStore();
const { entries: machineEntries, hasMachines, others: machineSessions } = storeToRefs(machines);
const showMachines = computed(() => hasMachines.value && !isArchivedView.value);
const liveMachineEntry = computed(() => machineEntries.value.find((entry) => entry.isLive) ?? machineEntries.value[0]);
const listedMachines = computed(() => (showMachines.value ? machineEntries.value : [liveMachineEntry.value]));
// Folding the live machine hides its whole tree; without other machines there's no heading to fold it with.
const liveMachineExpanded = computed(() => !showMachines.value || !sidebarStore.isGroupCollapsed(machineGroupKey(machines.liveKey)));

const liveSessionCount = computed(() => filteredProjectGroups.value.reduce((total, group) => total + group.sessionCount, 0)
  + filteredPinnedSessions.value.length);

function handleToggleLiveMachine(): void {
  sidebarStore.toggleGroupCollapsed(machineGroupKey(machines.liveKey));
}

let stopMachinePolling: (() => void) | null = null;
watch(hasMachines, (has) => {
  if (has && !stopMachinePolling) {
    stopMachinePolling = machines.startPolling();
  } else if (!has && stopMachinePolling) {
    stopMachinePolling();
    stopMachinePolling = null;
  }
}, { immediate: true });
onUnmounted(() => stopMachinePolling?.());

watch(
  () => sessionsStore.sessions.map((item) => item.session.id),
  (ids) => machines.rememberLiveSessions(ids),
);

const sessionsList = useTemplateRef<HTMLElement>("sessionsList");

/**
 * Opening another machine's session reloads the page on that machine. The list keeps its place across the reload, so
 * the row clicked stays under the pointer, and this machine's list is kept so it shows at once afterwards.
 */
function handleMachineSessionOpen(machine: MachineEntry, session: SessionListItem): void {
  const search = session.instanceId ? `?instanceId=${encodeURIComponent(session.instanceId)}` : "";
  if (sessionsList.value) saveSessionListScroll(sessionsList.value.scrollTop);
  machines.openOn(machine.key, `/sessions/${encodeURIComponent(session.session.id)}${search}`, {
    sessions: sessionsStore.sessions,
    projects: projects.value,
  });
}

/** Work here on another machine: the same switch as opening one of its sessions, landing on its first page. */
function handleWorkHere(machine: MachineEntry): void {
  if (sessionsList.value) saveSessionListScroll(sessionsList.value.scrollTop);
  machines.openOn(machine.key, "/", {
    sessions: sessionsStore.sessions,
    projects: projects.value,
  });
}

const searchQuery = shallowRef("");

function isProjectExpanded(projectId: string): boolean {
  return !sidebarStore.isGroupCollapsed(projectGroupKey(machines.liveKey, projectId));
}

const isNewProjectDialogOpen = shallowRef(false);


watch(
  [pathname, sessions],
  ([nextPath, nextSessions]) => {
    if (!nextPath.startsWith("/sessions/")) {
      return;
    }

    const sessionId = decodeURIComponent(nextPath.slice("/sessions/".length));
    const matchingSession = nextSessions.find((session) => session.session.id === sessionId);

    if (matchingSession) {
      activeSessionId.value = matchingSession.session.id;
      sidebarStore.setActiveRail("sessions");
    }
  },
  { immediate: true },
);

const normalizedQuery = computed(() => searchQuery.value.trim().toLowerCase());
const projectsById = computed(() => {
  return new Map(projects.value.map((project) => [project.id, project]));
});
const isLoading = computed(() => isSessionsLoading.value || areProjectsLoading.value);
const isNewSessionOpen = computed(() => pathname.value === "/sessions/new");

/**
 * The group the draft's session will land in: its project, or Scratch (where the server puts a
 * session without one), keyed the way the grouping below keys it.
 */
const draftGroupKey = computed<string | null>(() => {
  const draft = newSessionDraftRow.value;
  // A draft for another machine shows in that machine's group.
  if (!draft || newSessionMachine.value !== null) {
    return null;
  }
  return draftGroupKeyFor(draft.projectId, projects.value);
});
const errorMessage = computed(() => {
  const error = sessionsError.value ?? projectsError.value;
  // Working in another machine that stopped answering: say which, not the browser's "Failed to fetch".
  if (error && hasMachines.value && !machines.live.isHome && !machines.liveReachable) return `Can't reach ${machines.live.name}.`;
  return error;
});
const hasSessions = computed(() => sessions.value.length > 0);

/**
 * Back from a switch to another machine: the list goes back to where it was scrolled once the live machine's sessions
 * and projects are in (the other machines show their kept lists at once), so the row clicked is where it was. Gives
 * up after a few seconds, and when the user has scrolled since.
 */
const pendingScroll = takeSessionListScroll();
if (pendingScroll !== null) {
  let restored = false;
  const giveUp = setTimeout(() => restore(), 3_000);
  const restore = (): void => {
    if (restored) return;
    restored = true;
    clearTimeout(giveUp);
    void nextTick(() => scrollListTo(pendingScroll));
  };
  watch(
    () => (hasSessions.value || Boolean(errorMessage.value)) && !isLoading.value && projects.value.length > 0,
    (ready) => {
      if (ready) restore();
    },
    { immediate: true },
  );
  onUnmounted(() => clearTimeout(giveUp));
}

/** Scrolls the list to `top`, a frame at a time while it isn't tall enough yet; not once the user has scrolled it. */
function scrollListTo(top: number, framesLeft = 10, reached = 0): void {
  const list = sessionsList.value;
  if (!list || Math.abs(list.scrollTop - reached) >= 1) return;
  list.scrollTop = top;
  const now = list.scrollTop;
  if (now < top - 1 && framesLeft > 0) requestAnimationFrame(() => scrollListTo(top, framesLeft - 1, now));
}

const projectGroups = computed<ProjectTreeGroup[]>(() => buildProjectGroups(pinnedSplit.value.rest, projects.value, draftGroupKey.value));

function matchesQuery(session: SessionListItem): boolean {
  return sessionMatchesQuery(session, normalizedQuery.value, projectsById.value);
}

const filteredPinnedSessions = computed(() => normalizedQuery.value
  ? pinnedSplit.value.pinned.filter(matchesQuery)
  : pinnedSplit.value.pinned);

/** The Pinned group, shaped like a project. It shows while it has sessions, and during a drag so one can be dropped in. */
const pinnedGroup = computed<ProjectTreeGroup>(() => pinnedProjectGroup(filteredPinnedSessions.value));
const showPinnedGroup = computed(() => !isArchivedView.value
  && (filteredPinnedSessions.value.length > 0
    || (activeSessionDrag.value !== null && isSessionDragUnderway.value && !normalizedQuery.value)));

const filteredProjectGroups = computed<ProjectTreeGroup[]>(() => {
  if (!normalizedQuery.value) {
    // Archived sessions are the only reason to show a project there.
    return isArchivedView.value
      ? projectGroups.value.filter((project) => project.sessions.length > 0)
      : projectGroups.value;
  }

  return filterProjectGroups(projectGroups.value, normalizedQuery.value, matchesQuery);
});

function showArchived(show: boolean): void {
  selection.clear();
  sessionsStore.setRetentionStatus(show ? "archived" : "active");
}

// Shift-click ranges follow the rows as the list shows them.
watch(
  () => [...(showPinnedGroup.value ? [pinnedGroup.value] : []), ...filteredProjectGroups.value]
    .filter((group) => liveMachineExpanded.value && isProjectExpanded(group.id))
    .flatMap((group) => group.sessions.map((session) => session.session.id)),
  (order) => selection.setVisibleOrder(order),
  { immediate: true },
);

function archiveSelected(): void {
  archiveQueue.archive([...selection.selectedIds]);
  selection.clear();
}

async function restoreSelected(): Promise<void> {
  const ids = [...selection.selectedIds];
  selection.clear();
  for (const sessionId of ids) {
    try {
      await archiveQueue.restore(sessionId);
    } catch {
      // The archive queue shows the error; the rest still get restored.
    }
  }
}

/** The id of the group the draft row shows in (groups are keyed by project id, or "ungrouped"). */
const draftGroupId = computed<string | null>(() => draftGroupIdFor(draftGroupKey.value, projects.value));

// A draft shows in its group, so that group opens when a draft lands in it.
watch(draftGroupId, (groupId) => {
  if (groupId) {
    sidebarStore.setGroupCollapsed(machineGroupKey(machines.liveKey), false);
    sidebarStore.setGroupCollapsed(projectGroupKey(machines.liveKey, groupId), false);
  }
});

// Likewise the group of the other machine a draft starts on.
watch(newSessionMachine, (machineKey) => {
  if (machineKey && newSessionDraftRow.value) {
    sidebarStore.setGroupCollapsed(machineGroupKey(machineKey), false);
  }
});

function handleOpenDraft(): void {
  sidebarStore.setActiveRail("sessions");
  void router.navigate({
    to: "/sessions/new",
    search: {
      projectId: newSessionDraftRow.value?.projectId ?? undefined,
      source: undefined,
    },
  });
}

function handleToggleProject(projectId: string): void {
  sidebarStore.toggleGroupCollapsed(projectGroupKey(machines.liveKey, projectId));
}

function openNewSessionPage(projectId: string | null): void {
  sidebarStore.setActiveRail("sessions");
  void router.navigate({
    to: "/sessions/new",
    search: {
      projectId: projectId ?? undefined,
      source: undefined,
    },
  });
}

function handleNewSession(): void {
  openNewSessionPage(null);
}

/** A new session on a machine from its heading: the new-session page, with that machine picked. */
function handleNewSessionOn(machineKey: string): void {
  workspaceUiStore.setNewSessionMachine(machineKey);
  openNewSessionPage(null);
}

function handleProjectSessionCreate(projectId: string): void {
  openNewSessionPage(projectId);
}

function handleNewProject(): void {
  sidebarStore.setActiveRail("sessions");
  isNewProjectDialogOpen.value = true;
}

async function handleRetry(): Promise<void> {
  await Promise.all([refetchSessions(), refetchProjects()]);
}

async function handleProjectCreated(): Promise<void> {
  await refetchProjects();
  await refetchSessions();
}

async function handleProjectChanged(): Promise<void> {
  await refetchProjects();
  await refetchSessions();
}

function handleSessionSelect(session: SessionListItem): void {
  activeSessionId.value = session.session.id;
  sidebarStore.setActiveRail("sessions");

  void router.navigate({
    to: "/sessions/$id",
    params: { id: session.session.id },
    search: {
      instanceId: session.instanceId,
      parentSessionId: undefined,
    },
  });
}

const dragAnnouncement = shallowRef("");
const isDragMovePending = shallowRef(false);
const isCompleteDropZoneHovered = shallowRef(false);

function handleSessionDragStart(sessionId: string, projectId: string | null): void {
  const sessionExists = sessionsStore.sessions.some((session) => session.session.id === sessionId);
  if (!sessionExists) {
    activeSessionDrag.value = null;
    return;
  }

  activeSessionDrag.value = { sessionId, projectId };
  isSessionDragUnderway.value = false;
  clearTimeout(dragUnderwayTimer);
  dragUnderwayTimer = setTimeout(() => {
    isSessionDragUnderway.value = true;
  });
}

/** A fork or a started session dragged out of its parent: it stands on its own (Undo in the toast). */
function handleMoveOutOfParent(sessionId: string): void {
  activeSessionDrag.value = null;
  if (normalizedQuery.value) return;
  void lineageMoves.moveOut(sessionId);
}

function handleSessionDragEnd(): void {
  activeSessionDrag.value = null;
  isSessionDragUnderway.value = false;
  clearTimeout(dragUnderwayTimer);
}

/** Whether the session being dragged is pinned: dropping it on its own project unpins it. */
const activeDragPinned = computed(() => {
  const id = activeSessionDrag.value?.sessionId;
  const item = id ? sessionsStore.sessions.find((candidate) => candidate.session.id === id) : undefined;
  return item ? isPinned(item) : false;
});

/** A session dropped in the Pinned group: pinned where it was dropped. */
function handlePinSession(sessionId: string, beforeSessionId: string | null): void {
  activeSessionDrag.value = null;
  void pins.pin(sessionId, beforeSessionId);
}

/** A pinned session dropped back on its own project. */
function handleUnpinSession(sessionId: string): void {
  activeSessionDrag.value = null;
  void pins.unpin(sessionId);
}

async function handleMoveSession(sessionId: string, targetProjectId: string | null): Promise<void> {
  // Suppress moves while a search filter is active to avoid confusion with filtered views
  if (normalizedQuery.value) {
    return;
  }

  if (activeSessionDrag.value?.sessionId !== sessionId) {
    return;
  }

  const session = sessionsStore.sessions.find((candidate) => candidate.session.id === sessionId);
  if (!session) {
    activeSessionDrag.value = null;
    return;
  }

  const isKnownTarget = targetProjectId === null || projectsById.value.has(targetProjectId);
  if (!isKnownTarget) {
    activeSessionDrag.value = null;
    return;
  }

  // Prevent concurrent drag moves
  if (isDragMovePending.value) {
    return;
  }

  // Optimistically update the store so the UI moves the session immediately
  const previousProjectId = session.projectId ?? null;
  const previousProjectName = session.projectName ?? null;
  const targetProjectName = targetProjectId === null
    ? null
    : (projectsById.value.get(targetProjectId)?.name ?? previousProjectName);
  sessionsStore.patchSessionProject(sessionId, targetProjectId, targetProjectName);

  isDragMovePending.value = true;

  try {
    await moveSession(sessionId, targetProjectId);
    await refetchSessions();

    // Build announcement text for screen readers
    const targetProject = targetProjectId
      ? (projectsById.value.get(targetProjectId)?.name ?? "a project")
      : "Ungrouped";
    const sessionTitle = sessionsStore.sessions.find((s) => s.session.id === sessionId)?.session.title ?? "Session";
    dragAnnouncement.value = `Moved ${sessionTitle} to ${targetProject}`;
  } catch {
    // Rollback optimistic update on failure
    sessionsStore.patchSessionProject(sessionId, previousProjectId, previousProjectName);
    dragAnnouncement.value = "Move failed. Session returned to original project.";
  } finally {
    isDragMovePending.value = false;
    activeSessionDrag.value = null;
  }
}

function handleCompleteDropZoneDragOver(event: DragEvent): void {
  if (!activeSessionDrag.value) {
    return;
  }

  event.preventDefault();
  if (event.dataTransfer) {
    event.dataTransfer.dropEffect = "move";
  }
}

function handleCompleteDropZoneDragEnter(): void {
  if (activeSessionDrag.value) {
    isCompleteDropZoneHovered.value = true;
  }
}

function handleCompleteDropZoneDragLeave(): void {
  if (activeSessionDrag.value) {
    isCompleteDropZoneHovered.value = false;
  }
}

function handleCompleteDropZoneDrop(event: DragEvent): void {
  isCompleteDropZoneHovered.value = false;

  if (!activeSessionDrag.value) {
    return;
  }

  event.preventDefault();

  const sessionId = activeSessionDrag.value.sessionId;
  const session = sessionsStore.sessions.find((s) => s.session.id === sessionId);

  if (!session) {
    activeSessionDrag.value = null;
    return;
  }

  archiveQueue.archive([sessionId]);
  dragAnnouncement.value = `Archived ${session.session.title ?? "session"}`;

  // Clear drag state
  activeSessionDrag.value = null;
}

</script>

<template>
  <NewProjectDialog
    v-model:open="isNewProjectDialogOpen"
    @created="handleProjectCreated"
  />

  <section
    class="sessions-panel"
    aria-label="Sessions context panel"
  >
    <div
      v-if="isArchivedView"
      class="panel-archived-header"
    >
      <button
        type="button"
        class="panel-archived-header__back"
        data-testid="sessions-archived-back"
        @click="showArchived(false)"
      >
        <ArrowLeft aria-hidden="true" />
        <span>Archived sessions</span>
      </button>
    </div>
    <div
      v-else
      class="panel-header-row"
    >
      <div class="panel-actions">
        <Button
          variant="ghost"
          size="sm"
          class="panel-action-button"
          @click="handleNewSession"
        >
          <Plus
            class="panel-action-button__icon"
            aria-hidden="true"
          />
          <span>New session</span>
        </Button>

        <Button
          variant="ghost"
          size="sm"
          class="panel-action-button panel-action-button--icon"
          aria-label="New Project"
          title="New Project"
          @click="handleNewProject"
        >
          <FolderPlus
            class="panel-action-button__icon"
            aria-hidden="true"
          />
        </Button>
      </div>
    </div>

    <div class="panel-search">
      <Search
        class="panel-search__icon"
        aria-hidden="true"
      />
      <input
        v-model="searchQuery"
        type="search"
        placeholder="Filter sessions"
        aria-label="Filter sessions"
      >
    </div>

    <div
      ref="sessionsList"
      class="sessions-list"
    >
      <template
        v-for="machine in listedMachines"
        :key="machine.key"
      >
        <template v-if="machine.key === liveMachineEntry.key">
          <MachineHeader
            v-if="showMachines"
            :name="machine.name"
            live
            :unreachable="!machines.liveReachable"
            :note="machines.liveReachable ? null : 'unreachable'"
            :count="liveSessionCount"
            :expanded="liveMachineExpanded"
            menu
            @toggle="handleToggleLiveMachine"
            @new-session="handleNewSessionOn(machine.key)"
          />

          <template v-if="liveMachineExpanded">
            <div
              v-if="errorMessage && hasSessions"
              class="sessions-feedback-banner"
              aria-live="polite"
            >
              <p class="sessions-feedback-banner__copy">
                Showing cached sessions. Refresh failed: {{ errorMessage }}
              </p>
              <button
                type="button"
                class="sessions-feedback-banner__button"
                @click="handleRetry"
              >
                Retry
              </button>
            </div>

            <div
              v-if="isLoading && !hasSessions"
              class="sessions-feedback-state"
              aria-live="polite"
            >
              <LoaderCircle
                class="sessions-feedback-state__icon sessions-feedback-state__icon--spinning"
                aria-hidden="true"
              />
              <p class="sessions-feedback-state__title">
                Loading sessions
              </p>
              <p class="sessions-feedback-state__copy">
                Fetching the latest sessions and projects.
              </p>
            </div>

            <div
              v-else-if="errorMessage && !hasSessions"
              class="sessions-feedback-state sessions-feedback-state--error"
              aria-live="polite"
            >
              <p class="sessions-feedback-state__title">
                Unable to load sessions
              </p>
              <p class="sessions-feedback-state__copy">
                {{ errorMessage }}
              </p>
              <button
                type="button"
                class="sessions-feedback-state__button"
                @click="handleRetry"
              >
                Retry
              </button>
            </div>

            <template v-else>
              <ProjectGroup
                v-if="showPinnedGroup"
                key="pinned"
                :project="pinnedGroup"
                pinned
                :expanded="isProjectExpanded(PINNED_GROUP_ID)"
                :active-session-id="activeSessionId"
                :active-drag-session-id="activeSessionDrag?.sessionId ?? null"
                :active-drag-project-id="activeSessionDrag?.projectId ?? null"
                :row-keys="sessionRowKeys"
                :running-counts="runningCounts"
                :running-subagents="runningSubagents"
                data-testid="pinned-group"
                @session-changed="handleRetry"
                @toggle="handleToggleProject"
                @select-session="handleSessionSelect"
                @drag-session-start="handleSessionDragStart"
                @drag-session-end="handleSessionDragEnd"
                @pin-session="handlePinSession"
              />
              <ProjectGroup
                v-for="project in filteredProjectGroups"
                :key="project.id"
                :project="project"
                :expanded="isProjectExpanded(project.id)"
                :active-session-id="activeSessionId"
                :active-drag-session-id="activeSessionDrag?.sessionId ?? null"
                :active-drag-project-id="activeSessionDrag?.projectId ?? null"
                :draft="project.id === draftGroupId ? newSessionDraftRow : null"
                :draft-active="isNewSessionOpen"
                :row-keys="sessionRowKeys"
                :running-counts="runningCounts"
                :running-subagents="runningSubagents"
                :active-drag-pinned="activeDragPinned"
                @new-session="handleProjectSessionCreate"
                @open-draft="handleOpenDraft"
                @project-changed="handleProjectChanged"
                @session-changed="handleRetry"
                @toggle="handleToggleProject"
                @select-session="handleSessionSelect"
                @drag-session-start="handleSessionDragStart"
                @drag-session-end="handleSessionDragEnd"
                @move-session="handleMoveSession"
                @move-out-of-parent="handleMoveOutOfParent"
                @unpin-session="handleUnpinSession"
              />
            </template>

            <div
              v-if="!isLoading && !errorMessage && filteredProjectGroups.length === 0 && !showPinnedGroup"
              class="sessions-empty-state"
            >
              <p class="sessions-empty-state__title">
                <template v-if="normalizedQuery">No sessions found</template>
                <template v-else>{{ isArchivedView ? "No archived sessions" : "No sessions yet" }}</template>
              </p>
              <p class="sessions-empty-state__copy">
                {{ normalizedQuery ? "Try a different search term or clear the filter." : isArchivedView ? "Sessions you archive show up here." : "Start one with New session above." }}
              </p>
            </div>
          </template>
        </template>
        <MachineSessionsGroup
          v-else
          :machine="machine"
          :state="machineSessions[machine.key]"
          :query="normalizedQuery"
          :draft="machine.key === newSessionMachine ? newSessionDraftRow : null"
          :draft-active="isNewSessionOpen"
          @open="handleMachineSessionOpen(machine, $event)"
          @open-draft="handleOpenDraft"
          @new-session="handleNewSessionOn(machine.key)"
          @work-here="handleWorkHere(machine)"
        />
      </template>

      <!-- Complete drop zone -->
      <Transition name="complete-drop-zone">
        <div
          v-if="activeSessionDrag"
          class="complete-drop-zone"
          :class="{ 'complete-drop-zone--hovered': isCompleteDropZoneHovered }"
          role="button"
          aria-label="Drop session here to archive it"
          :aria-dropeffect="isCompleteDropZoneHovered ? 'move' : 'none'"
          @dragover="handleCompleteDropZoneDragOver"
          @dragenter="handleCompleteDropZoneDragEnter"
          @dragleave="handleCompleteDropZoneDragLeave"
          @drop="handleCompleteDropZoneDrop"
        >
          <Archive
            class="complete-drop-zone__icon"
            aria-hidden="true"
          />
          <span class="complete-drop-zone__label">Archive</span>
        </div>
      </Transition>
    </div>

    <div
      v-if="selection.isSelecting"
      class="selection-bar"
      role="toolbar"
      aria-label="Selected sessions"
      data-testid="session-selection-bar"
    >
      <span class="selection-bar__count">{{ selection.count }} selected</span>
      <Button
        v-if="isArchivedView"
        size="sm"
        class="selection-bar__action"
        data-testid="session-selection-restore"
        @click="restoreSelected"
      >
        <ArchiveRestore aria-hidden="true" />
        Restore
      </Button>
      <Button
        v-else
        size="sm"
        class="selection-bar__action"
        data-testid="session-selection-archive"
        @click="archiveSelected"
      >
        <Archive aria-hidden="true" />
        Archive
      </Button>
      <button
        type="button"
        class="selection-bar__clear"
        aria-label="Clear selection"
        title="Clear selection (Esc)"
        @click="selection.clear()"
      >
        <X aria-hidden="true" />
      </button>
    </div>

    <div
      v-else-if="!isArchivedView"
      class="panel-footer"
    >
      <button
        type="button"
        class="panel-footer__link"
        data-testid="sessions-show-archived"
        @click="showArchived(true)"
      >
        <Archive aria-hidden="true" />
        Archived
      </button>
    </div>

    <!-- Screen reader live region for drag-and-drop announcements -->
    <div
      aria-live="polite"
      aria-atomic="true"
      class="sessions-sr-only"
    >
      {{ dragAnnouncement }}
    </div>
  </section>
</template>

<style scoped>
.sessions-panel {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-height: 0;
  background: transparent;
}

.panel-header-row {
  padding-top: 8px;
}

.panel-archived-header {
  padding: 8px 8px 4px;
}

.panel-archived-header__back {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  height: 30px;
  padding: 0 8px;
  border: 0;
  border-radius: var(--radius-btn);
  background: transparent;
  color: var(--text);
  font-size: 13px;
  font-weight: 600;
  cursor: pointer;
  transition: background var(--transition);
}

.panel-archived-header__back:hover {
  background: color-mix(in srgb, var(--text) 5%, transparent);
}

.panel-archived-header__back svg {
  width: 14px;
  height: 14px;
  color: var(--muted);
}

.panel-footer {
  flex-shrink: 0;
  padding: 6px 8px 8px;
  border-top: 1px solid var(--border);
}

.panel-footer__link {
  display: inline-flex;
  align-items: center;
  gap: 7px;
  height: 28px;
  padding: 0 8px;
  border: 0;
  border-radius: var(--radius-btn);
  background: transparent;
  color: var(--muted);
  font-size: 12px;
  cursor: pointer;
  transition: background var(--transition), color var(--transition);
}

.panel-footer__link:hover {
  background: color-mix(in srgb, var(--text) 5%, transparent);
  color: var(--text);
}

.panel-footer__link svg {
  width: 13px;
  height: 13px;
}

.selection-bar {
  display: flex;
  flex-shrink: 0;
  align-items: center;
  gap: 6px;
  margin: 6px 8px 8px;
  padding: 5px 5px 5px 12px;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  background: var(--card-bg);
  box-shadow: 0 12px 32px -12px rgba(0, 0, 0, 0.35);
  font-size: 12px;
}

.selection-bar__count {
  flex: 1;
  font-weight: 600;
  font-variant-numeric: tabular-nums;
}

.selection-bar__action {
  gap: 6px;
  height: 28px;
  border-radius: 999px;
}

.selection-bar__action svg {
  width: 13px;
  height: 13px;
}

.selection-bar__clear {
  display: grid;
  place-items: center;
  width: 28px;
  height: 28px;
  padding: 0;
  border: 0;
  border-radius: 7px;
  background: transparent;
  color: var(--muted);
  cursor: pointer;
}

.selection-bar__clear:hover {
  background: color-mix(in srgb, var(--text) 6%, transparent);
  color: var(--text);
}

.selection-bar__clear svg {
  width: 14px;
  height: 14px;
}

.panel-header {
  margin: 0;
  padding: 8px 12px 6px;
  font-size: 10px;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  color: var(--muted);
}

.panel-actions {
  display: flex;
  gap: 6px;
  padding: 0 8px 8px;
}

.panel-action-button {
  flex: 1 1 auto;
  min-height: 32px;
  padding: 0 12px;
  justify-content: center;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--card-bg);
  color: var(--text);
  font-size: 13px;
  font-weight: 500;
  transition: background var(--transition), border-color var(--transition), color var(--transition);
}

.panel-action-button:hover {
  background: var(--card-bg);
  border-color: color-mix(in srgb, var(--text) 18%, transparent);
  color: var(--text);
}

.panel-action-button--icon {
  flex: 0 0 32px;
  width: 32px;
  padding: 0;
  border-color: transparent;
  background: transparent;
  color: var(--muted);
}

.panel-action-button--icon:hover {
  border-color: transparent;
  background: color-mix(in srgb, var(--text) 5%, transparent);
  color: var(--text);
}

.panel-action-button__icon {
  width: 15px;
  height: 15px;
}

.panel-search {
  margin: 0 8px 10px;
  position: relative;
}

.panel-search__icon {
  position: absolute;
  top: 50%;
  left: 10px;
  width: 13px;
  height: 13px;
  color: var(--muted);
  transform: translateY(-50%);
  pointer-events: none;
}

.panel-search input {
  width: 100%;
  height: 32px;
  background: color-mix(in srgb, var(--text) 5%, transparent);
  border: 1px solid transparent;
  border-radius: var(--radius-btn);
  padding: 0 10px 0 30px;
  font-size: 13px;
  color: var(--text);
  outline: none;
  transition: background var(--transition), border-color var(--transition);
}

.panel-search input::placeholder {
  color: var(--muted);
}

.panel-search input:focus {
  background: var(--panel-bg);
  border-color: color-mix(in srgb, var(--accent) 55%, transparent);
}

.sessions-list {
  flex: 1;
  overflow-y: auto;
  padding: 0 8px 12px;
  gap: 4px;
  scrollbar-width: thin;
  scrollbar-color: var(--muted) transparent;
}

.sessions-empty-state {
  padding: 16px;
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.sessions-feedback-state {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 8px;
  padding: 16px;
}

.sessions-feedback-banner {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 8px 12px;
  margin-bottom: 8px;
  border: 1px solid rgba(239, 68, 68, 0.2);
  background: rgba(239, 68, 68, 0.08);
}

.sessions-feedback-banner__copy {
  margin: 0;
  font-size: 11px;
  color: var(--text);
}

.sessions-feedback-banner__button {
  min-height: 28px;
  padding: 0 10px;
  border: 1px solid transparent;
  border-radius: 0;
  background: transparent;
  color: var(--muted);
  font-size: 11px;
  font-weight: 500;
  transition: background var(--transition), color var(--transition), border-color var(--transition);
}

.sessions-feedback-banner__button:hover {
  background: var(--bg);
  border-color: var(--border);
  color: var(--text);
}

.sessions-feedback-banner__button:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}

.sessions-feedback-state__icon {
  width: 16px;
  height: 16px;
  color: var(--muted);
}

.sessions-feedback-state__icon--spinning {
  animation: sessions-spin 0.9s linear infinite;
}

.sessions-feedback-state__title {
  margin: 0;
  font-size: 12px;
  font-weight: 600;
  color: var(--text);
}

.sessions-feedback-state__copy {
  margin: 0;
  font-size: 11px;
  color: var(--muted);
}

.sessions-feedback-state__button {
  min-height: 30px;
  padding: 0 10px;
  border: 1px solid transparent;
  border-radius: 0;
  background: transparent;
  color: var(--muted);
  font-size: 11px;
  font-weight: 500;
  transition: background var(--transition), color var(--transition), border-color var(--transition);
}

.sessions-feedback-state__button:hover {
  background: var(--bg);
  border-color: var(--border);
  color: var(--text);
}

.sessions-feedback-state__button:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}

.sessions-empty-state__title {
  margin: 0;
  font-size: 12px;
  font-weight: 600;
  color: var(--text);
}

.sessions-empty-state__copy {
  margin: 0;
  font-size: 11px;
  color: var(--muted);
}

@keyframes sessions-spin {
  from {
    transform: rotate(0deg);
  }

  to {
    transform: rotate(360deg);
  }
}

.sessions-sr-only {
  position: absolute;
  width: 1px;
  height: 1px;
  padding: 0;
  margin: -1px;
  overflow: hidden;
  clip: rect(0, 0, 0, 0);
  white-space: nowrap;
  border: 0;
}

.complete-drop-zone {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 8px;
  margin-top: 8px;
  padding: 16px;
  border: 2px dashed var(--muted);
  background: transparent;
  color: var(--muted);
  font-size: 12px;
  font-weight: 500;
  transition: all 0.2s ease;
  cursor: pointer;
}

.complete-drop-zone__icon {
  width: 16px;
  height: 16px;
  flex-shrink: 0;
}

.complete-drop-zone__label {
  user-select: none;
}

.complete-drop-zone--hovered {
  border-color: var(--accent);
  background: rgba(var(--accent-rgb, 139, 92, 246), 0.08);
  color: var(--accent);
}

.complete-drop-zone-enter-active,
.complete-drop-zone-leave-active {
  transition: opacity 0.2s ease, transform 0.2s ease;
}

.complete-drop-zone-enter-from {
  opacity: 0;
  transform: translateY(8px);
}

.complete-drop-zone-leave-to {
  opacity: 0;
  transform: translateY(8px);
}
</style>

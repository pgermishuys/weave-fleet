<script setup lang="ts">
import { computed, onMounted, onUnmounted, reactive, shallowRef, watch } from "vue";
import { useLocation, useRouter } from "@tanstack/vue-router";
import { Archive, ArchiveRestore, ArrowLeft, FolderPlus, LoaderCircle, Plus, Search, X } from "lucide-vue-next";
import { storeToRefs } from "pinia";
import type { SessionListItem } from "@/api/client";
import { useProjects } from "@/composables/use-projects";
import { useSessions } from "@/composables/use-sessions";
import { useMoveSession } from "@/composables/use-session-actions";
import { useArchiveQueueStore } from "@/stores/archive-queue";
import { useSessionSelectionStore } from "@/stores/session-selection";
import { useSessionsStore } from "@/stores/sessions";
import { useSidebarStore } from "@/stores/sidebar";
import { useWorkspaceUiStore } from "@/stores/workspace-ui";
import { useMachinesStore, type MachineEntry } from "@/stores/machines";

import { Button } from "@/components/ui/button";
import MachineHeader from "./MachineHeader.vue";
import MachineSessionsGroup from "./MachineSessionsGroup.vue";
import NewProjectDialog from "./NewProjectDialog.vue";

import ProjectGroup from "./ProjectGroup.vue";

interface ProjectReorderTarget {
  projectId: string;
  position: number;
}

interface ProjectTreeGroup {
  id: string;
  projectId: string | null;
  name: string;
  isUngrouped: boolean;
  canMoveUp: boolean;
  canMoveDown: boolean;
  moveUpTargets: ProjectReorderTarget[];
  moveDownTargets: ProjectReorderTarget[];
  sessionCount: number;
  sessions: SessionListItem[];
}

interface ActiveSessionDrag {
  sessionId: string;
  projectId: string | null;
}


const sessionsStore = useSessionsStore();
const archiveQueue = useArchiveQueueStore();
const selection = useSessionSelectionStore();
const sidebarStore = useSidebarStore();
const { newSessionDraftRow, sessionRowKeys } = storeToRefs(useWorkspaceUiStore());
const router = useRouter();

let releaseSessionList: (() => void) | null = null;
onMounted(() => {
  releaseSessionList = sidebarStore.registerSessionList();
});
onUnmounted(() => {
  releaseSessionList?.();
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

// Other machines: every machine is listed, the live one in full, the rest as their last polled list.
const machines = useMachinesStore();
const { entries: machineEntries, hasMachines, others: machineSessions } = storeToRefs(machines);
const showMachines = computed(() => hasMachines.value && !isArchivedView.value);
const liveMachineIndex = computed(() => Math.max(0, machineEntries.value.findIndex((entry) => entry.isLive)));
const liveMachineEntry = computed(() => machineEntries.value[liveMachineIndex.value]);
const machinesBefore = computed(() => (showMachines.value ? machineEntries.value.slice(0, liveMachineIndex.value) : []));
const machinesAfter = computed(() => (showMachines.value ? machineEntries.value.slice(liveMachineIndex.value + 1) : []));

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

function handleMachineSessionOpen(machine: MachineEntry, session: SessionListItem): void {
  const search = session.instanceId ? `?instanceId=${encodeURIComponent(session.instanceId)}` : "";
  machines.openOn(machine.key, `/sessions/${encodeURIComponent(session.session.id)}${search}`);
}

const searchQuery = shallowRef("");
const expandedProjects = reactive<Record<string, boolean>>({});
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
const userProjects = computed(() => {
  return projects.value.filter((project) => project.type !== "scratch");
});
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
  if (!draft) {
    return null;
  }
  if (draft.projectId && projectsById.value.has(draft.projectId)) {
    return draft.projectId;
  }
  return projects.value.find((project) => project.type === "scratch")?.id ?? "Ungrouped";
});
const errorMessage = computed(() => sessionsError.value ?? projectsError.value);
const hasSessions = computed(() => sessions.value.length > 0);

function getProjectDisplayName(session: SessionListItem): string {
  if (!session.projectId) {
    return "Ungrouped";
  }

  if (session.projectName?.trim()) {
    return session.projectName;
  }

  const project = projectsById.value.get(session.projectId);
  if (project) {
    return project.name;
  }

  return "Ungrouped";
}


function buildReorderTargets(
  projectOrder: Array<{ projectId: string | null }>,
): ProjectReorderTarget[] {
  return projectOrder.flatMap((project, index) => {
    if (!project.projectId) {
      return [];
    }

    return [{
      projectId: project.projectId,
      position: index + 1,
    }];
  });
}

function swapProjects<T>(projects: readonly T[], leftIndex: number, rightIndex: number): T[] {
  const nextProjects = [...projects];
  const leftProject = nextProjects[leftIndex];

  nextProjects[leftIndex] = nextProjects[rightIndex];
  nextProjects[rightIndex] = leftProject;

  return nextProjects;
}

const projectGroups = computed<ProjectTreeGroup[]>(() => {
  const groupedSessions = new Map<string, {
    id: string;
    projectId: string | null;
    name: string;
    sortPosition: number;
    isUngrouped: boolean;
    sessions: SessionListItem[];
  }>();

  for (const project of userProjects.value) {
    groupedSessions.set(project.id, {
      id: project.id,
      projectId: project.id,
      name: project.name,
      sortPosition: project.position,
      isUngrouped: false,
      sessions: [],
    });
  }

  for (const session of sessions.value) {
    const project = session.projectId ? projectsById.value.get(session.projectId) : undefined;
    const projectName = getProjectDisplayName(session);
    const groupKey = session.projectId ?? projectName;
    const existing = groupedSessions.get(groupKey);

    if (existing) {
      existing.sessions.push(session);
      continue;
    }

    groupedSessions.set(groupKey, {
      id: session.projectId ?? "ungrouped",
      projectId: session.projectId ?? null,
      name: projectName,
      sortPosition: project?.position ?? Number.MAX_SAFE_INTEGER,
      isUngrouped: projectName === "Ungrouped",
      sessions: [session],
    });
  }

  // The draft's group shows even before it has a session (Scratch, the first time).
  const draftKey = draftGroupKey.value;
  if (draftKey && !groupedSessions.has(draftKey)) {
    const project = projectsById.value.get(draftKey);
    groupedSessions.set(draftKey, {
      id: project?.id ?? "ungrouped",
      projectId: project?.id ?? null,
      name: project?.name ?? "Ungrouped",
      sortPosition: project?.position ?? Number.MAX_SAFE_INTEGER,
      isUngrouped: !project,
      sessions: [],
    });
  }

  const sortedGroups = [...groupedSessions.values()]
    .sort((left, right) => {
      if (left.isUngrouped) {
        return 1;
      }

      if (right.isUngrouped) {
        return -1;
      }

      if (left.sortPosition !== right.sortPosition) {
        return left.sortPosition - right.sortPosition;
      }

      return left.name.localeCompare(right.name);
    });

  const orderedUserGroups = sortedGroups.filter((projectGroup) => !projectGroup.isUngrouped);

  return sortedGroups.map((projectGroup) => {
      const orderedIndex = orderedUserGroups.findIndex((candidate) => candidate.id === projectGroup.id);
      const canMoveUp = orderedIndex > 0;
      const canMoveDown = orderedIndex >= 0 && orderedIndex < orderedUserGroups.length - 1;
      const moveUpTargets = canMoveUp
        ? buildReorderTargets(swapProjects(orderedUserGroups, orderedIndex, orderedIndex - 1))
        : [];
      const moveDownTargets = canMoveDown
        ? buildReorderTargets(swapProjects(orderedUserGroups, orderedIndex, orderedIndex + 1))
        : [];
      return {
        id: projectGroup.id,
        projectId: projectGroup.projectId,
        name: projectGroup.name,
        isUngrouped: projectGroup.isUngrouped,
        canMoveUp,
        canMoveDown,
        moveUpTargets,
        moveDownTargets,
        sessionCount: projectGroup.sessions.length,
        sessions: projectGroup.sessions,
      } satisfies ProjectTreeGroup;
    });
});

const filteredProjectGroups = computed<ProjectTreeGroup[]>(() => {
  if (!normalizedQuery.value) {
    // Archived sessions are the only reason to show a project there.
    return isArchivedView.value
      ? projectGroups.value.filter((project) => project.sessions.length > 0)
      : projectGroups.value;
  }

  return projectGroups.value
    .map((project) => {
      const projectMatch = project.name.toLowerCase().includes(normalizedQuery.value);
      const sessions = projectMatch
        ? project.sessions
        : project.sessions.filter((session) => {
            const searchable = [
              session.session.title,
              session.session.id,
              getProjectDisplayName(session),
              session.sessionStatus,
            ].join(" ").toLowerCase();

            return searchable.includes(normalizedQuery.value);
          });

      return {
        ...project,
        sessionCount: sessions.length,
        ...(projectMatch && project.sessions.length === 0 ? { sessionCount: 0 } : {}),
        sessions,
      } satisfies ProjectTreeGroup;
    })
    .filter((project) => project.sessions.length > 0 || project.name.toLowerCase().includes(normalizedQuery.value));
});

function showArchived(show: boolean): void {
  selection.clear();
  sessionsStore.setRetentionStatus(show ? "archived" : "active");
}

// Shift-click ranges follow the rows as the list shows them.
watch(
  () => filteredProjectGroups.value
    .filter((group) => expandedProjects[group.id] ?? true)
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
const draftGroupId = computed<string | null>(() => {
  const key = draftGroupKey.value;
  return key === null ? null : projectsById.value.has(key) ? key : "ungrouped";
});

// A draft shows in its group, so that group opens when a draft lands in it.
watch(draftGroupId, (groupId) => {
  if (groupId) {
    expandedProjects[groupId] = true;
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
  expandedProjects[projectId] = !(expandedProjects[projectId] ?? true);
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
const activeSessionDrag = shallowRef<ActiveSessionDrag | null>(null);
const isCompleteDropZoneHovered = shallowRef(false);

function handleSessionDragStart(sessionId: string, projectId: string | null): void {
  const sessionExists = sessionsStore.sessions.some((session) => session.session.id === sessionId);
  if (!sessionExists) {
    activeSessionDrag.value = null;
    return;
  }

  activeSessionDrag.value = { sessionId, projectId };
}

function handleSessionDragEnd(): void {
  activeSessionDrag.value = null;
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
  sessionsStore.patchSession(sessionId, {
    projectId: targetProjectId,
    projectName: targetProjectName,
  });

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
    sessionsStore.patchSession(sessionId, {
      projectId: previousProjectId,
      projectName: previousProjectName,
    });
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
          <span>New Session</span>
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

    <div class="sessions-list">
      <template v-if="showMachines">
        <MachineSessionsGroup
          v-for="machine in machinesBefore"
          :key="machine.key"
          :machine="machine"
          :state="machineSessions[machine.key]"
          :query="normalizedQuery"
          @open="handleMachineSessionOpen(machine, $event)"
        />
        <MachineHeader
          v-if="liveMachineEntry"
          :name="liveMachineEntry.name"
          live
        />
      </template>

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

      <ProjectGroup
        v-for="project in filteredProjectGroups"
        v-else
        :key="project.id"
        :project="project"
        :expanded="expandedProjects[project.id] ?? true"
        :active-session-id="activeSessionId"
        :active-drag-session-id="activeSessionDrag?.sessionId ?? null"
        :active-drag-project-id="activeSessionDrag?.projectId ?? null"
        :draft="project.id === draftGroupId ? newSessionDraftRow : null"
        :draft-active="isNewSessionOpen"
        :row-keys="sessionRowKeys"
        @new-session="handleProjectSessionCreate"
        @open-draft="handleOpenDraft"
        @project-changed="handleProjectChanged"
        @session-changed="handleRetry"
        @toggle="handleToggleProject"
        @select-session="handleSessionSelect"
        @drag-session-start="handleSessionDragStart"
        @drag-session-end="handleSessionDragEnd"
        @move-session="handleMoveSession"
      />

      <div
        v-if="!isLoading && !errorMessage && filteredProjectGroups.length === 0"
        class="sessions-empty-state"
      >
        <p class="sessions-empty-state__title">
          {{ isArchivedView && !normalizedQuery ? "No archived sessions" : "No sessions found" }}
        </p>
        <p
          v-if="!isArchivedView || normalizedQuery"
          class="sessions-empty-state__copy"
        >
          Try a different search term or clear the filter.
        </p>
      </div>

      <MachineSessionsGroup
        v-for="machine in machinesAfter"
        :key="machine.key"
        :machine="machine"
        :state="machineSessions[machine.key]"
        :query="normalizedQuery"
        @open="handleMachineSessionOpen(machine, $event)"
      />

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

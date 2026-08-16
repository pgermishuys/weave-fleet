<script setup lang="ts">
import { computed, reactive, shallowRef, watch } from "vue";
import { useLocation, useRouter } from "@tanstack/vue-router";
import { Check, LoaderCircle, Plus, Search } from "lucide-vue-next";
import { storeToRefs } from "pinia";
import type { SessionListItem } from "@/api/client";
import { useProjects } from "@/composables/use-projects";
import { useSessions } from "@/composables/use-sessions";
import { useArchiveSession, useMoveSession } from "@/composables/use-session-actions";
import { useSessionsStore } from "@/stores/sessions";
import { useSidebarStore } from "@/stores/sidebar";

import { Button } from "@/components/ui/button";
import ConfirmCompleteSessionDialog from "./ConfirmCompleteSessionDialog.vue";
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
const sidebarStore = useSidebarStore();
const router = useRouter();
const pathname = useLocation({
  select: (location) => location.pathname,
});

const { moveSession } = useMoveSession();
const { archiveSession, isArchiving } = useArchiveSession();

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

    if (retentionStatus.value === "all") {
      return true;
    }

    return session.retentionStatus === retentionStatus.value;
  });
});

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
    return projectGroups.value;
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

function handleToggleProject(projectId: string): void {
  expandedProjects[projectId] = !(expandedProjects[projectId] ?? true);
}

function openNewSessionDialog(projectId: string | null): void {
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
  openNewSessionDialog(null);
}

function handleProjectSessionCreate(projectId: string): void {
  openNewSessionDialog(projectId);
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
const pendingCompleteSessionId = shallowRef<string | null>(null);
const isCompleteDialogOpen = shallowRef(false);

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

  // Open the confirmation dialog
  pendingCompleteSessionId.value = sessionId;
  isCompleteDialogOpen.value = true;

  // Clear drag state
  activeSessionDrag.value = null;
}

async function handleCompleteConfirm(deleteWorktree: boolean): Promise<void> {
  const sessionId = pendingCompleteSessionId.value;
  if (!sessionId) {
    return;
  }

  const session = sessionsStore.sessions.find((s) => s.session.id === sessionId);
  if (!session) {
    isCompleteDialogOpen.value = false;
    pendingCompleteSessionId.value = null;
    return;
  }

  try {
    await archiveSession(sessionId);
    sessionsStore.patchSession(sessionId, { retentionStatus: "archived" });

    const sessionTitle = session.session.title ?? "Session";
    dragAnnouncement.value = `Completed ${sessionTitle}`;

    // Navigate away if the completed session was active
    if (activeSessionId.value === sessionId) {
      const remainingSessions = sessions.value.filter((s) => s.session.id !== sessionId);
      if (remainingSessions.length > 0) {
        void router.navigate({
          to: "/sessions/$id",
          params: { id: remainingSessions[0].session.id },
          search: {
            instanceId: remainingSessions[0].instanceId,
            parentSessionId: undefined,
          },
        });
      } else {
        void router.navigate({ to: "/" });
      }
    }

    // TODO: if deleteWorktree, call backend to remove the worktree
    void deleteWorktree;

    isCompleteDialogOpen.value = false;
    pendingCompleteSessionId.value = null;
  } catch {
    // Errors are handled by the mutation composable state
    dragAnnouncement.value = "Failed to complete session";
  }
}

function handleCompleteCancel(): void {
  isCompleteDialogOpen.value = false;
  pendingCompleteSessionId.value = null;
}

</script>

<template>
  <!-- NewSessionDialog disconnected - now using /sessions/new route -->
  <!--
  <NewSessionDialog
    v-model:open="newSessionDialogModel"
    :initial-project-id="newSessionDialogProjectId"
    :initial-source="newSessionDialogInitialSource"
    @created="handleSessionCreated"
  />
  -->
  <NewProjectDialog
    v-model:open="isNewProjectDialogOpen"
    @created="handleProjectCreated"
  />
  <ConfirmCompleteSessionDialog
    v-model:open="isCompleteDialogOpen"
    :session-id="pendingCompleteSessionId ?? ''"
    :session-title="sessionsStore.sessions.find((s) => s.session.id === pendingCompleteSessionId)?.session.title ?? 'Session'"
    :has-worktree="sessionsStore.sessions.find((s) => s.session.id === pendingCompleteSessionId)?.isolationStrategy === 'worktree'"
    :is-archiving="isArchiving"
    @confirm="handleCompleteConfirm"
    @cancel="handleCompleteCancel"
  />

  <section
    class="sessions-panel"
    aria-label="Sessions context panel"
  >
    <div class="panel-header-row">
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
          class="panel-action-button panel-action-button--secondary"
          @click="handleNewProject"
        >
          <Plus
            class="panel-action-button__icon"
            aria-hidden="true"
          />
          <span>New Project</span>
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
        @new-session="handleProjectSessionCreate"
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
          No sessions found
        </p>
        <p class="sessions-empty-state__copy">
          Try a different search term or clear the filter.
        </p>
      </div>

      <!-- Complete drop zone -->
      <Transition name="complete-drop-zone">
        <div
          v-if="activeSessionDrag"
          class="complete-drop-zone"
          :class="{ 'complete-drop-zone--hovered': isCompleteDropZoneHovered }"
          role="button"
          aria-label="Drop session here to complete and archive it"
          :aria-dropeffect="isCompleteDropZoneHovered ? 'move' : 'none'"
          @dragover="handleCompleteDropZoneDragOver"
          @dragenter="handleCompleteDropZoneDragEnter"
          @dragleave="handleCompleteDropZoneDragLeave"
          @drop="handleCompleteDropZoneDrop"
        >
          <Check
            class="complete-drop-zone__icon"
            aria-hidden="true"
          />
          <span class="complete-drop-zone__label">Complete</span>
        </div>
      </Transition>
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
  background: var(--panel-bg);
}

.panel-header-row {
  padding-top: 4px;
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
  gap: 8px;
  padding: 0 8px 6px;
}

.panel-action-button {
  min-height: 28px;
  padding: 0 10px;
  border: 1px solid var(--border);
  background: rgba(255, 255, 255, 0.04);
  color: var(--text);
  font-size: 11px;
  font-weight: 500;
}

.panel-action-button:hover {
  background: var(--bg);
  border-color: var(--border);
  color: var(--text);
}

.panel-action-button--secondary {
  background: transparent;
  color: var(--muted);
}

.panel-action-button--secondary:hover {
  background: var(--bg);
  border-color: var(--border);
  color: var(--text);
}

.panel-action-button__icon {
  width: 14px;
  height: 14px;
}

.panel-search {
  margin: 0 8px 6px;
  position: relative;
}

.panel-search__icon {
  position: absolute;
  top: 50%;
  left: 8px;
  width: 12px;
  height: 12px;
  color: var(--muted);
  transform: translateY(-50%);
}

.panel-search input {
  width: 100%;
  background: var(--card-bg);
  border: 1px solid var(--border);
  border-radius: 0;
  padding: 5px 8px 5px 28px;
  font-size: 12px;
  color: var(--text);
  outline: none;
}

.panel-search input:focus {
  border-color: var(--accent);
}

.sessions-list {
  flex: 1;
  overflow-y: auto;
  padding: 0 12px 12px;
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

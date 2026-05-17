<script setup lang="ts">
import { computed, onUnmounted, reactive, shallowRef, watch } from "vue";
import { useLocation, useRouter } from "@tanstack/vue-router";
import { LoaderCircle, Plus, Search } from "lucide-vue-next";
import { storeToRefs } from "pinia";
import type { CreateSessionResponse, ProjectResponse, SessionListItem } from "@/lib/api-types";
import { useActivityStream } from "@/composables/use-activity-stream";
import { useProjects } from "@/composables/use-projects";
import { useSessions } from "@/composables/use-sessions";
import { useMoveSession } from "@/composables/use-session-actions";
import { useSessionsStore } from "@/stores/sessions";
import { useSidebarStore } from "@/stores/sidebar";
import { useWorkspaceUiStore } from "@/stores/workspace-ui";
import NewProjectDialog from "./NewProjectDialog.vue";
import NewSessionDialog from "./NewSessionDialog.vue";
import ProjectGroup from "./ProjectGroup.vue";

interface ProjectReorderTarget {
  projectId: string;
  position: number;
}

interface ProjectTreeGroup {
  id: string;
  projectId: string | null;
  name: string;
  color: string;
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

const PROJECT_COLOR_PALETTE = [
  "#8b5cf6",
  "#22c55e",
  "#38bdf8",
  "#f59e0b",
  "#ef4444",
  "#14b8a6",
  "#f97316",
  "#a855f7",
] as const;

const sessionsStore = useSessionsStore();
const sidebarStore = useSidebarStore();
const workspaceUiStore = useWorkspaceUiStore();
const activityStream = useActivityStream();
const router = useRouter();
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

    if (retentionStatus.value === "all") {
      return true;
    }

    return session.retentionStatus === retentionStatus.value;
  });
});

const searchQuery = shallowRef("");
const expandedProjects = reactive<Record<string, boolean>>({});
const isNewProjectDialogOpen = shallowRef(false);
const { newSessionDialogOpen, newSessionDialogProjectId, newSessionDialogInitialSource } = storeToRefs(workspaceUiStore);
const newSessionDialogModel = computed({
  get: () => newSessionDialogOpen.value,
  set: (open: boolean) => {
    workspaceUiStore.setNewSessionDialogOpen(open);
  },
});

const sessionActivityEvents = [
  "session.created",
  "session.updated",
  "session.deleted",
  "session_archived",
  "session_unarchived",
  "session_deleted",
] as const;

function handleSessionActivity(): void {
  void refetchSessions();
}

for (const eventType of sessionActivityEvents) {
  activityStream.on(eventType, handleSessionActivity);
}

onUnmounted(() => {
  for (const eventType of sessionActivityEvents) {
    activityStream.off(eventType, handleSessionActivity);
  }
});

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

function getProjectColor(projectName: string, project?: ProjectResponse): string {
  if (!project) {
    return projectName === "Ungrouped" ? "#71717a" : PROJECT_COLOR_PALETTE[getColorIndex(projectName)];
  }

  return PROJECT_COLOR_PALETTE[Math.abs(project.position) % PROJECT_COLOR_PALETTE.length];
}

function getColorIndex(value: string): number {
  let hash = 0;

  for (const character of value) {
    hash = (hash * 31 + character.charCodeAt(0)) | 0;
  }

  return Math.abs(hash) % PROJECT_COLOR_PALETTE.length;
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
    color: string;
    sortPosition: number;
    isUngrouped: boolean;
    sessions: SessionListItem[];
  }>();

  for (const project of userProjects.value) {
    groupedSessions.set(project.id, {
      id: project.id,
      projectId: project.id,
      name: project.name,
      color: getProjectColor(project.name, project),
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
      color: getProjectColor(projectName, project),
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
        color: projectGroup.color,
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
  workspaceUiStore.openNewSessionDialog(projectId);
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

async function handleSessionCreated(response: CreateSessionResponse): Promise<void> {
  activeSessionId.value = response.session.id;
  sidebarStore.setActiveRail("sessions");

  try {
    await refetchSessions();
  } finally {
    void router.navigate({
      to: "/sessions/$id",
      params: { id: response.session.id },
      search: {
        instanceId: response.instanceId,
        parentSessionId: undefined,
      },
    });
  }
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

</script>

<template>
  <NewSessionDialog
    v-model:open="newSessionDialogModel"
    :initial-project-id="newSessionDialogProjectId"
    :initial-source="newSessionDialogInitialSource"
    @created="handleSessionCreated"
  />
  <NewProjectDialog
    v-model:open="isNewProjectDialogOpen"
    @created="handleProjectCreated"
  />

  <section
    class="sessions-panel"
    aria-label="Sessions context panel"
  >
    <div class="panel-header-row">
      <div class="panel-actions">
        <button
          type="button"
          class="panel-action-button"
          @click="handleNewSession"
        >
          <Plus
            class="panel-action-button__icon"
            aria-hidden="true"
          />
          <span>New Session</span>
        </button>

        <button
          type="button"
          class="panel-action-button panel-action-button--secondary"
          @click="handleNewProject"
        >
          <Plus
            class="panel-action-button__icon"
            aria-hidden="true"
          />
          <span>New Project</span>
        </button>
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
  display: inline-flex;
  align-items: center;
  gap: 6px;
  min-height: 28px;
  padding: 0 10px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: rgba(255, 255, 255, 0.04);
  color: var(--text);
  font-size: 11px;
  font-weight: 500;
}

.panel-action-button:hover {
  background: rgba(255, 255, 255, 0.08);
}

.panel-action-button:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}

.panel-action-button--secondary {
  background: transparent;
  color: var(--muted);
}

.panel-action-button--secondary:hover {
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
  left: 10px;
  width: 14px;
  height: 14px;
  color: var(--muted);
  transform: translateY(-50%);
}

.panel-search input {
  width: 100%;
  background: var(--card-bg);
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  padding: 7px 10px 7px 30px;
  color: var(--text);
  outline: none;
}

.panel-search input:focus {
  border-color: var(--accent);
}

.sessions-list {
  flex: 1;
  overflow-y: auto;
  padding: 0 0 12px;
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
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: transparent;
  color: var(--text);
  font-size: 11px;
  font-weight: 500;
}

.sessions-feedback-banner__button:hover {
  background: rgba(255, 255, 255, 0.06);
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
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: rgba(255, 255, 255, 0.04);
  color: var(--text);
  font-size: 11px;
  font-weight: 500;
}

.sessions-feedback-state__button:hover {
  background: rgba(255, 255, 255, 0.08);
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
</style>

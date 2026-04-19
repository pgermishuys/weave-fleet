import type { Session } from "@/lib/types";
import { defineStore } from "pinia";
import { computed, reactive, shallowRef } from "vue";
import { resolveAgentColor } from "@/lib/agent-colors";
import { mockSessions } from "@/lib/board-mock-data";

export type BoardStatus = "active" | "idle" | "waiting_input" | "completed" | "error";
export type BoardGroupBy = "status" | "project" | "agent";
export type BoardSortBy = "newest" | "oldest" | "title";

export interface BoardSession {
  id: string;
  title: string;
  projectName: string;
  projectColor: string;
  status: BoardStatus;
  agent: string;
  modelName: string;
  createdAt: Date;
  completedAt?: Date;
  prompt: string;
  totalTokens: number;
  cost: number;
  durationSeconds: number;
  progressPercent: number;
  progressLabel: string;
}

export interface BoardGroup {
  key: string;
  label: string;
  sessions: BoardSession[];
}

export interface BoardStatusOption {
  value: BoardStatus;
  label: string;
  color: string;
}

export interface BoardSortOption {
  value: BoardSortBy;
  label: string;
}

export const boardStatusOptions: readonly BoardStatusOption[] = [
  { value: "active", label: "Active", color: "var(--running)" },
  { value: "idle", label: "Idle", color: "#71717a" },
  { value: "waiting_input", label: "Waiting", color: "#f59e0b" },
  { value: "completed", label: "Complete", color: "var(--complete)" },
  { value: "error", label: "Error", color: "var(--error)" },
] as const;

export const boardSortOptions: readonly BoardSortOption[] = [
  { value: "newest", label: "Newest first" },
  { value: "oldest", label: "Oldest first" },
  { value: "title", label: "Title (A–Z)" },
] as const;

const groupByLabels: Record<BoardGroupBy, string> = {
  status: "Status",
  project: "Project",
  agent: "Agent",
};

const boardReferenceTime = new Date("2026-02-27T11:05:00");

const agentModelNames: Record<string, string> = {
  loom: "Claude Sonnet 4.5",
  shuttle: "GPT-5.4 Mini",
  tapestry: "GPT-5.4",
};

function formatProjectName(name: string): string {
  return name
    .replace(/^proj[-_]/, "")
    .split(/[-_]/)
    .filter(Boolean)
    .map((segment) => segment.charAt(0).toUpperCase() + segment.slice(1))
    .join(" ");
}

function normalizeStatus(status: string): BoardStatus {
  switch (status) {
    case "idle":
      return "idle";
    case "waiting_input":
      return "waiting_input";
    case "completed":
      return "completed";
    case "error":
      return "error";
    case "active":
    default:
      return "active";
  }
}

function getModelName(agent: string): string {
  return agentModelNames[agent] ?? "GPT-5.4";
}

function getTotalTokens(session: Session): number {
  return session.tokens.input + session.tokens.output + session.tokens.reasoning + session.tokens.cache;
}

function getDurationSeconds(session: Session): number {
  const endTime = session.completedAt ?? boardReferenceTime;
  const durationSeconds = Math.round((endTime.getTime() - session.createdAt.getTime()) / 1000);

  return Math.max(durationSeconds, 60);
}

function getProgress(session: Session): { progressLabel: string; progressPercent: number } {
  if (session.planProgress) {
    const progressPercent = Math.round((session.planProgress.done / session.planProgress.total) * 100);

    return {
      progressLabel: `${session.planProgress.done}/${session.planProgress.total} tasks`,
      progressPercent,
    };
  }

  switch (session.status) {
    case "idle":
      return {
        progressLabel: "Queued",
        progressPercent: 12,
      };
    case "waiting_input":
      return {
        progressLabel: "Awaiting review",
        progressPercent: 82,
      };
    case "completed":
      return {
        progressLabel: "Complete",
        progressPercent: 100,
      };
    case "error":
      return {
        progressLabel: "Failed",
        progressPercent: 100,
      };
    case "active":
    default:
      return {
        progressLabel: "In progress",
        progressPercent: 56,
      };
  }
}

function createBoardSessions(): BoardSession[] {
  return mockSessions.map((session) => ({
    id: session.id,
    title: session.initialPrompt,
    projectName: formatProjectName(session.name),
    projectColor: resolveAgentColor(session.name),
    status: normalizeStatus(session.status),
    agent: session.currentAgent,
    modelName: getModelName(session.currentAgent),
    createdAt: session.createdAt,
    completedAt: session.completedAt,
    prompt: session.initialPrompt,
    totalTokens: getTotalTokens(session),
    cost: session.cost,
    durationSeconds: getDurationSeconds(session),
    progressPercent: getProgress(session).progressPercent,
    progressLabel: getProgress(session).progressLabel,
  }));
}

export function getBoardStatusMeta(status: BoardStatus): BoardStatusOption {
  return boardStatusOptions.find((option) => option.value === status) ?? boardStatusOptions[0];
}

export const useBoardStore = defineStore("board", () => {
  const sessions = shallowRef<BoardSession[]>(createBoardSessions());
  const selectedProject = shallowRef("all");
  const groupBy = shallowRef<BoardGroupBy>("status");
  const sortBy = shallowRef<BoardSortBy>("newest");
  const statusFilters = reactive<Record<BoardStatus, boolean>>({
    active: true,
    idle: true,
    waiting_input: true,
    completed: true,
    error: true,
  });
  const agentFilters = reactive<Record<string, boolean>>(
    Object.fromEntries(
      [...new Set(sessions.value.map((session) => session.agent))]
        .sort((left, right) => left.localeCompare(right))
        .map((agent) => [agent, true]),
    ) as Record<string, boolean>,
  );

  const availableProjects = computed<string[]>(() => {
    return [...new Set(sessions.value.map((session) => session.projectName))].sort((left, right) => {
      return left.localeCompare(right);
    });
  });

  const availableAgents = computed<string[]>(() => {
    return [...new Set(sessions.value.map((session) => session.agent))].sort((left, right) => {
      return left.localeCompare(right);
    });
  });

  const filteredSessions = computed<BoardSession[]>(() => {
    return sessions.value.filter((session) => {
      const projectMatches = selectedProject.value === "all" || session.projectName === selectedProject.value;
      const statusMatches = statusFilters[session.status] ?? false;
      const agentMatches = agentFilters[session.agent] ?? false;

      return projectMatches && statusMatches && agentMatches;
    });
  });

  const sortedSessions = computed<BoardSession[]>(() => {
    return [...filteredSessions.value].sort((left, right) => {
      switch (sortBy.value) {
        case "oldest":
          return left.createdAt.getTime() - right.createdAt.getTime();
        case "title":
          return left.title.localeCompare(right.title);
        case "newest":
        default:
          return right.createdAt.getTime() - left.createdAt.getTime();
      }
    });
  });

  const groupedSessions = computed<BoardGroup[]>(() => {
    const groups = new Map<string, BoardSession[]>();

    for (const session of sortedSessions.value) {
      const key = groupBy.value === "status"
        ? session.status
        : groupBy.value === "project"
          ? session.projectName
          : session.agent;
      const existingSessions = groups.get(key) ?? [];

      existingSessions.push(session);
      groups.set(key, existingSessions);
    }

    const grouped = [...groups.entries()].map(([key, groupSessions]) => ({
      key,
      label: groupBy.value === "status" ? getBoardStatusMeta(key as BoardStatus).label : key,
      sessions: groupSessions,
    }));

    if (groupBy.value === "status") {
      return grouped.sort((left, right) => {
        const leftIndex = boardStatusOptions.findIndex((option) => option.value === left.key);
        const rightIndex = boardStatusOptions.findIndex((option) => option.value === right.key);

        return leftIndex - rightIndex;
      });
    }

    return grouped.sort((left, right) => left.label.localeCompare(right.label));
  });

  const quickStats = computed(() => {
    const visibleSessions = filteredSessions.value;

    return {
      total: sessions.value.length,
      visible: visibleSessions.length,
      active: visibleSessions.filter((session) => session.status === "active").length,
      completed: visibleSessions.filter((session) => session.status === "completed").length,
      projects: new Set(visibleSessions.map((session) => session.projectName)).size,
    };
  });

  const filterSummary = computed(() => {
    return `${quickStats.value.visible} visible • grouped by ${groupByLabels[groupBy.value].toLowerCase()}`;
  });

  function setSelectedProject(project: string): void {
    selectedProject.value = project;
  }

  function setStatusFilter(status: BoardStatus, enabled: boolean): void {
    statusFilters[status] = enabled;
  }

  function setAgentFilter(agent: string, enabled: boolean): void {
    agentFilters[agent] = enabled;
  }

  function setGroupBy(value: BoardGroupBy): void {
    groupBy.value = value;
  }

  function setSortBy(value: BoardSortBy): void {
    sortBy.value = value;
  }

  return {
    sessions,
    selectedProject,
    statusFilters,
    agentFilters,
    groupBy,
    sortBy,
    availableProjects,
    availableAgents,
    filteredSessions,
    sortedSessions,
    groupedSessions,
    quickStats,
    filterSummary,
    setSelectedProject,
    setStatusFilter,
    setAgentFilter,
    setGroupBy,
    setSortBy,
  };
});

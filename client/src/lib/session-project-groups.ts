/**
 * How the sessions list groups a machine's sessions: by project, in the projects' own order, with Ungrouped last. The
 * live machine and every other machine group the same way, so a machine looks the same whether it's live or not.
 */
import type { ProjectResponse, SessionListItem } from "@/api/client";

export interface ProjectReorderTarget {
  projectId: string;
  position: number;
}

export interface ProjectTreeGroup {
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

/** The Pinned group's id, for folding it like a project. */
export const PINNED_GROUP_ID = "pinned";

/** What a project group needs to know about a project. */
export type ProjectSummary = Pick<ProjectResponse, "id" | "name" | "type" | "position">;

export function projectDisplayName(session: SessionListItem, projectsById: ReadonlyMap<string, ProjectSummary>): string {
  if (!session.projectId) {
    return "Ungrouped";
  }

  if (session.projectName?.trim()) {
    return session.projectName;
  }

  return projectsById.get(session.projectId)?.name ?? "Ungrouped";
}

/**
 * The group a new-session draft's session will land in: its project, or Scratch (where the server puts a session
 * without one), keyed the way {@link buildProjectGroups} keys it.
 */
export function draftGroupKeyFor(projectId: string | null | undefined, projects: readonly ProjectSummary[]): string {
  if (projectId && projects.some((project) => project.id === projectId)) {
    return projectId;
  }
  return projects.find((project) => project.type === "scratch")?.id ?? "Ungrouped";
}

/** The id of the group a draft row shows in (groups are keyed by project id, or "ungrouped"). */
export function draftGroupIdFor(key: string | null, projects: readonly ProjectSummary[]): string | null {
  return key === null ? null : projects.some((project) => project.id === key) ? key : "ungrouped";
}

function buildReorderTargets(projectOrder: Array<{ projectId: string | null }>): ProjectReorderTarget[] {
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

/**
 * `sessions` (the ones outside the Pinned group) in their projects. Every user project shows, even an empty one; Scratch
 * only while it has sessions, or while the draft (`draftKey`) will land in it.
 */
export function buildProjectGroups(
  sessions: readonly SessionListItem[],
  projects: readonly ProjectSummary[],
  draftKey: string | null = null,
): ProjectTreeGroup[] {
  const projectsById = new Map(projects.map((project) => [project.id, project]));
  const groupedSessions = new Map<string, {
    id: string;
    projectId: string | null;
    name: string;
    sortPosition: number;
    isUngrouped: boolean;
    sessions: SessionListItem[];
  }>();

  for (const project of projects) {
    if (project.type === "scratch") continue;
    groupedSessions.set(project.id, {
      id: project.id,
      projectId: project.id,
      name: project.name,
      sortPosition: project.position,
      isUngrouped: false,
      sessions: [],
    });
  }

  for (const session of sessions) {
    const project = session.projectId ? projectsById.get(session.projectId) : undefined;
    const projectName = projectDisplayName(session, projectsById);
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
  if (draftKey && !groupedSessions.has(draftKey)) {
    const project = projectsById.get(draftKey);
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
}

/** Whether `session` matches the search box's lower-case `query`: its title, id, project or status. */
export function sessionMatchesQuery(
  session: SessionListItem,
  query: string,
  projectsById: ReadonlyMap<string, ProjectSummary>,
): boolean {
  const searchable = [
    session.session.title,
    session.session.id,
    projectDisplayName(session, projectsById),
    session.sessionStatus,
  ].join(" ").toLowerCase();

  return searchable.includes(query);
}

/** The groups the search box leaves: a project whose name matches keeps all its sessions, the rest only the matches. */
export function filterProjectGroups(
  groups: readonly ProjectTreeGroup[],
  query: string,
  matches: (session: SessionListItem) => boolean,
): ProjectTreeGroup[] {
  return groups
    .map((project) => {
      const projectMatch = project.name.toLowerCase().includes(query);
      const sessions = projectMatch
        ? project.sessions
        : project.sessions.filter(matches);

      return {
        ...project,
        sessionCount: sessions.length,
        sessions,
      } satisfies ProjectTreeGroup;
    })
    .filter((project) => project.sessions.length > 0 || project.name.toLowerCase().includes(query));
}

/** A machine's Pinned group, shaped like a project. */
export function pinnedProjectGroup(sessions: SessionListItem[]): ProjectTreeGroup {
  return {
    id: PINNED_GROUP_ID,
    projectId: null,
    name: "Pinned",
    isUngrouped: false,
    canMoveUp: false,
    canMoveDown: false,
    moveUpTargets: [],
    moveDownTargets: [],
    sessionCount: sessions.length,
    sessions,
  };
}

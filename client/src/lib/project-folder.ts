import type { ScannedRepository, SessionListItem } from "@/api/client";
import type { NewSessionFolder } from "@/lib/new-session-request";

type ProjectSession = Pick<SessionListItem, "projectId" | "session" | "sourceDirectory" | "workspaceDirectory" | "isolationStrategy" | "origin">;

/** A path as written on any OS: forward slashes, no trailing one, any case (Windows folders compare without it). */
function comparable(path: string): string {
  return path.replace(/\\/g, "/").replace(/\/+$/, "").toLowerCase();
}

function timestamp(value: number | string | undefined): number {
  const number = typeof value === "number" ? value : Number(value);
  return Number.isFinite(number) ? number : 0;
}

/** The folder a session started in, as the new-session page picks it; null for a quick chat or a repository that's gone. */
function folderOf(item: ProjectSession, repositories: readonly ScannedRepository[]): NewSessionFolder | null {
  const origin = item.origin ?? null;
  if (origin?.providerId === "builtin.quickchat") {
    return null;
  }

  // A worktree remembers its repository; a session in an existing worktree only through where it came from.
  const candidates = [
    item.sourceDirectory,
    origin?.sourceType === "repository" ? origin.resourceId : null,
    item.workspaceDirectory,
  ];
  for (const candidate of candidates) {
    if (!candidate) continue;
    const repository = repositories.find((entry) => comparable(entry.path) === comparable(candidate));
    if (repository) {
      return { kind: "repository", path: repository.path };
    }
  }

  // A plain folder the session ran in as it was.
  const isPlainFolder = !item.sourceDirectory
    && item.isolationStrategy === "existing"
    && (origin === null || origin.providerId === "builtin.local");
  return isPlainFolder && item.workspaceDirectory ? { kind: "directory", path: item.workspaceDirectory } : null;
}

/**
 * The folder a project's sessions use: the one its newest session started in. Null when none of its sessions has a
 * folder that's still there, so the page keeps the folder it had.
 */
export function projectFolder(
  sessions: readonly ProjectSession[],
  projectId: string,
  repositories: readonly ScannedRepository[],
): NewSessionFolder | null {
  const newestFirst = sessions
    .filter((item) => item.projectId === projectId)
    .sort((left, right) => timestamp(right.session.time?.created) - timestamp(left.session.time?.created));
  for (const item of newestFirst) {
    const folder = folderOf(item, repositories);
    if (folder) {
      return folder;
    }
  }
  return null;
}

import { api, type FolderInspection, type WorkspaceRootsResponse } from "@/api/client";
import type { NewSessionFolder } from "@/lib/new-session-request";

/** Whether a folder exists, is a git repository, and is inside the workspace roots. */
export async function inspectFolder(path: string): Promise<FolderInspection> {
  const { data, error } = await api.GET("/api/directories/inspect", { params: { query: { path } } });
  if (error || !data) {
    throw new Error(error ? String(error) : "Couldn't check that folder.");
  }
  return data as FolderInspection;
}

/** Adds a folder to the workspace roots, so sessions can run in it. Rescanning is up to the caller. */
export async function addFolderToFleet(path: string): Promise<void> {
  const { error } = await api.POST("/api/workspace-roots", { body: { path } as never });
  if (error) {
    const message = (error as { error?: string }).error;
    throw new Error(message ?? "Couldn't add that folder.");
  }
}

/** The folder a session uses for an inspected path: a repository when it's a git checkout. */
export function folderForInspection(inspection: FolderInspection): NewSessionFolder {
  return inspection.isGitRepo
    ? { kind: "repository", path: inspection.path }
    : { kind: "directory", path: inspection.path };
}

/** A folder Fleet just made. */
export interface NewFolder {
  path: string;
  isGitRepo: boolean;
  /** It was outside the workspace roots, so Fleet added it. */
  addedToFleet: boolean;
  /** Something to tell the person, such as a missing first commit. */
  warning: string | null;
}

/** How far a clone has got, in git's words. */
export interface CloneProgress {
  phase: string;
  percent: number;
}

/** Creating or cloning into a folder that's already there. */
export class FolderExistsError extends Error {
  constructor(message: string, readonly path: string) {
    super(message);
  }
}

function failure(error: unknown, response: Response, path: string, fallback: string): Error {
  const message = (error as { error?: string } | undefined)?.error ?? fallback;
  return response.status === 409 ? new FolderExistsError(message, path) : new Error(message);
}

/** The workspace roots that exist on disk: where new folders can go. */
export async function listWorkspaceRoots(): Promise<string[]> {
  const { data, error } = await api.GET("/api/workspace-roots");
  if (error || !data) {
    throw new Error("Couldn't load your workspace roots.");
  }
  const { roots } = data as WorkspaceRootsResponse;
  return roots.filter((root) => root.exists).map((root) => root.path);
}

/** Creates a folder, and parent folders it needs; with `git`, a repository with an empty first commit. */
export async function createFolder(path: string, git: boolean): Promise<NewFolder> {
  const { data, error, response } = await api.POST("/api/directories", { body: { path, git } });
  if (error || !data) {
    throw failure(error, response, path, "Couldn't create that folder.");
  }
  return data as NewFolder;
}

type CloneLine =
  | { type: "progress"; phase: string; percent: number }
  | { type: "done"; folder: NewFolder }
  | { type: "error"; error: string };

/**
 * Clones a repository (`owner/repo`, or an https or ssh address) into a new folder at `path`,
 * reporting git's progress. Resolves once the clone is done.
 */
export async function cloneRepository(
  repository: string,
  path: string,
  onProgress: (progress: CloneProgress) => void,
): Promise<NewFolder> {
  const { data, error, response } = await api.POST("/api/directories/clone", {
    body: { repository, path },
    parseAs: "stream",
  });
  if (error || !data) {
    throw failure(error, response, path, "Couldn't clone that repository.");
  }

  const reader = (data as ReadableStream<Uint8Array>).getReader();
  const decoder = new TextDecoder();
  let buffered = "";
  for (;;) {
    const { value, done } = await reader.read();
    buffered += decoder.decode(value, { stream: !done });
    const lines = buffered.split("\n");
    buffered = done ? "" : lines.pop() ?? "";
    for (const line of lines.filter((candidate) => candidate.trim())) {
      const parsed = JSON.parse(line) as CloneLine;
      if (parsed.type === "progress") {
        onProgress({ phase: parsed.phase, percent: parsed.percent });
      } else if (parsed.type === "done") {
        return parsed.folder;
      } else {
        throw new Error(parsed.error);
      }
    }
    if (done) {
      throw new Error("The clone stopped before it finished.");
    }
  }
}

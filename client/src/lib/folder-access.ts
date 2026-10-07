import { api, type ApiClient, type DirectoryEntry, type DirectoryListResponse, type FolderInspection, type WorkspaceRootsResponse } from "@/api/client";
import type { NewSessionFolder } from "@/lib/new-session-request";

/**
 * Folders on a machine: `client` is the machine to ask (the live one unless the new-session box picked another).
 */

/** Whether a folder exists, is a git repository, and is inside the locations. */
export async function inspectFolder(path: string, client: ApiClient = api): Promise<FolderInspection> {
  const { data, error } = await client.GET("/api/directories/inspect", { params: { query: { path } } });
  if (error || !data) {
    throw new Error(error ? String(error) : "Couldn't check that folder.");
  }
  return data as FolderInspection;
}

/** Adds a folder to the locations, so sessions can run in it. Rescanning is up to the caller. */
export async function addFolderToFleet(path: string, client: ApiClient = api): Promise<void> {
  const { error } = await client.POST("/api/workspace-roots", { body: { path } as never });
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
  /** It was outside the locations, so Fleet added it. */
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

/** One folder as the folder box lists it: what's in it, or the nearest folder above it that's there. */
export interface FolderListing {
  /** The folder, as the machine writes it (`~` expanded). */
  path: string;
  exists: boolean;
  /** When it isn't there, the deepest folder above it that is. */
  nearestExisting: string | null;
  entries: DirectoryEntry[];
}

/** The folders in `path`, anywhere on the machine. */
export async function listFolder(path: string, client: ApiClient = api, signal?: AbortSignal): Promise<FolderListing> {
  const { data, error } = await client.GET("/api/directories", {
    params: { query: { path, unconstrained: true } },
    signal,
  });
  if (error || !data) {
    throw new Error("Couldn't list that folder.");
  }
  const listing = data as DirectoryListResponse;
  return {
    path: listing.currentPath ?? path,
    exists: listing.exists,
    nearestExisting: listing.nearestExisting,
    entries: listing.entries,
  };
}

/** What a new folder gets on a machine unless told otherwise, and the home folder `~` stands for there. */
export interface NewFolderDefaults {
  /** git's own default branch, else main. */
  firstBranch: string;
  home: string | null;
}

export async function newFolderDefaults(client: ApiClient = api): Promise<NewFolderDefaults> {
  const { data } = await client.GET("/api/directories/defaults");
  const defaults = data as Partial<NewFolderDefaults> | undefined;
  return { firstBranch: defaults?.firstBranch ?? "main", home: defaults?.home ?? null };
}

/** The locations that exist on disk: where new folders can go. */
export async function listWorkspaceRoots(client: ApiClient = api): Promise<string[]> {
  const { data, error } = await client.GET("/api/workspace-roots");
  if (error || !data) {
    throw new Error("Couldn't load your locations.");
  }
  const { roots } = data as WorkspaceRootsResponse;
  return roots.filter((root) => root.exists).map((root) => root.path);
}

/**
 * Creates a folder, and parent folders it needs; with `git`, a repository with an empty first commit on
 * `branch` (the machine's default when there's none).
 */
export async function createFolder(path: string, git: boolean, client: ApiClient = api, branch?: string): Promise<NewFolder> {
  const { data, error, response } = await client.POST("/api/directories", { body: { path, git, branch: branch || null } });
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
  client: ApiClient = api,
): Promise<NewFolder> {
  const { data, error, response } = await client.POST("/api/directories/clone", {
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

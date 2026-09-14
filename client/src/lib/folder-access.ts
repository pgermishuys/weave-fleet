import { api, type FolderInspection } from "@/api/client";
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

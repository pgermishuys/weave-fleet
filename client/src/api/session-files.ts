import { api } from "./client";
import type { BrowseDirectoryResponse, FileContentResponse } from "./client";

/**
 * Browse the directory structure of a session's workspace.
 *
 * @param sessionId - The session ID
 * @param path - Optional relative path within the workspace (defaults to root)
 * @returns Directory listing with entries and current path
 * @throws Error if the request fails
 */
export async function browseSessionDirectory(
  sessionId: string,
  path?: string
): Promise<BrowseDirectoryResponse> {
  const { data, error, response } = await api.GET("/api/sessions/{id}/files/browse", {
    params: {
      path: { id: sessionId },
      query: path ? { path } : undefined,
    },
  });

  if (error || !data) {
    throw new Error(`Failed to browse directory: ${response.status} ${response.statusText}`);
  }

  return data;
}

/**
 * Read the content of a file from a session's workspace.
 *
 * @param sessionId - The session ID
 * @param path - Relative path to the file within the workspace
 * @returns File content with metadata (binary status, truncation)
 * @throws Error if the request fails
 */
export async function readSessionFile(
  sessionId: string,
  path: string
): Promise<FileContentResponse> {
  const { data, error, response } = await api.GET("/api/sessions/{id}/files/content", {
    params: {
      path: { id: sessionId },
      query: { path },
    },
  });

  if (error || !data) {
    throw new Error(`Failed to read file: ${response.status} ${response.statusText}`);
  }

  return data;
}

export type WriteSessionFileResult =
  | { saved: true; hash: string }
  | { saved: false; content: string | null; hash: string };

/**
 * Save a file from the editor. `baseHash` is the hash the read returned for the text the edit
 * started from; if the file changed since, nothing is written and the result carries what's on
 * disk now.
 *
 * @throws Error with the server's reason when the save is refused (outside the session, .git, too large).
 */
export async function writeSessionFile(
  sessionId: string,
  path: string,
  content: string,
  baseHash: string,
): Promise<WriteSessionFileResult> {
  const { data, error, response } = await api.PUT("/api/sessions/{id}/files/content", {
    params: { path: { id: sessionId } },
    body: { path, content, baseHash },
  });

  if (response.status === 409 && error) {
    const conflict = error as { content?: string | null; hash?: string };
    return { saved: false, content: conflict.content ?? null, hash: conflict.hash ?? "" };
  }

  if (error || !data) {
    const reason = (error as { error?: string } | undefined)?.error;
    throw new Error(reason ?? `Failed to save file: ${response.status} ${response.statusText}`);
  }

  return { saved: true, hash: data.hash };
}

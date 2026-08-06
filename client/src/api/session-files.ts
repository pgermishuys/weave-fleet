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

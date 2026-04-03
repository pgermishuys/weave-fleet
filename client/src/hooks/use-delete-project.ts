"use client";

import { useState } from "react";
import { apiFetch } from "@/lib/api-client";

export type DeleteProjectMode = "move_to_scratch" | "delete_sessions";

export interface UseDeleteProjectResult {
  deleteProject: (projectId: string, mode?: DeleteProjectMode) => Promise<void>;
  isDeleting: boolean;
  error?: string;
}

export function useDeleteProject(): UseDeleteProjectResult {
  const [isDeleting, setIsDeleting] = useState(false);
  const [error, setError] = useState<string | undefined>();

  const deleteProject = async (
    projectId: string,
    mode: DeleteProjectMode = "move_to_scratch"
  ): Promise<void> => {
    setIsDeleting(true);
    setError(undefined);

    try {
      const params = new URLSearchParams({ mode });
      const response = await apiFetch(
        `/api/projects/${encodeURIComponent(projectId)}?${params.toString()}`,
        { method: "DELETE" }
      );

      if (!response.ok) {
        const body = await response.json().catch(() => ({}));
        throw new Error((body as { error?: string }).error ?? `HTTP ${response.status}`);
      }
    } catch (err) {
      const message = err instanceof Error ? err.message : "Failed to delete project";
      setError(message);
      throw err;
    } finally {
      setIsDeleting(false);
    }
  };

  return { deleteProject, isDeleting, error };
}

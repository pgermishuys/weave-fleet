"use client";

import { useState } from "react";
import { apiFetch } from "@/lib/api-client";

export interface UseReorderProjectResult {
  reorderProject: (projectId: string, newPosition: number) => Promise<void>;
  isReordering: boolean;
  error?: string;
}

export function useReorderProject(): UseReorderProjectResult {
  const [isReordering, setIsReordering] = useState(false);
  const [error, setError] = useState<string | undefined>();

  const reorderProject = async (
    projectId: string,
    newPosition: number
  ): Promise<void> => {
    setIsReordering(true);
    setError(undefined);

    try {
      const response = await apiFetch(
        `/api/projects/${encodeURIComponent(projectId)}/reorder`,
        {
          method: "PATCH",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ position: newPosition }),
        }
      );

      if (!response.ok) {
        const body = await response.json().catch(() => ({}));
        throw new Error((body as { error?: string }).error ?? `HTTP ${response.status}`);
      }
    } catch (err) {
      const message = err instanceof Error ? err.message : "Failed to reorder project";
      setError(message);
      throw err;
    } finally {
      setIsReordering(false);
    }
  };

  return { reorderProject, isReordering, error };
}

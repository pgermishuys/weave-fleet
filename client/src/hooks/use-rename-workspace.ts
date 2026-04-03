"use client";

import { useState } from "react";
import { apiFetch } from "@/lib/api-client";

export interface UseRenameWorkspaceResult {
  renameWorkspace: (
    workspaceId: string,
    displayName: string,
    onSuccess?: () => void
  ) => Promise<void>;
  isLoading: boolean;
  error?: string;
}

export function useRenameWorkspace(): UseRenameWorkspaceResult {
  const [isLoading, setIsLoading] = useState(false);
  const [error, setError] = useState<string | undefined>(undefined);

  const renameWorkspace = async (
    workspaceId: string,
    displayName: string,
    onSuccess?: () => void
  ): Promise<void> => {
    setIsLoading(true);
    setError(undefined);

    try {
      const response = await apiFetch(
        `/api/workspaces/${encodeURIComponent(workspaceId)}`,
        {
          method: "PATCH",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ displayName }),
        }
      );

      if (!response.ok) {
        const body = await response.json().catch(() => ({}));
        throw new Error(
          (body as { error?: string }).error ?? `HTTP ${response.status}`
        );
      }

      onSuccess?.();
    } catch (err) {
      const message =
        err instanceof Error ? err.message : "Failed to rename workspace";
      setError(message);
      throw err;
    } finally {
      setIsLoading(false);
    }
  };

  return { renameWorkspace, isLoading, error };
}

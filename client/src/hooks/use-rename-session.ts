"use client";

import { useState } from "react";
import { apiFetch } from "@/lib/api-client";

export interface UseRenameSessionResult {
  renameSession: (
    sessionId: string,
    title: string,
    onSuccess?: () => void
  ) => Promise<void>;
  isLoading: boolean;
  error?: string;
}

export function useRenameSession(): UseRenameSessionResult {
  const [isLoading, setIsLoading] = useState(false);
  const [error, setError] = useState<string | undefined>(undefined);

  const renameSession = async (
    sessionId: string,
    title: string,
    onSuccess?: () => void
  ): Promise<void> => {
    setIsLoading(true);
    setError(undefined);

    try {
      const response = await apiFetch(
        `/api/sessions/${encodeURIComponent(sessionId)}`,
        {
          method: "PATCH",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ title }),
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
        err instanceof Error ? err.message : "Failed to rename session";
      setError(message);
      throw err;
    } finally {
      setIsLoading(false);
    }
  };

  return { renameSession, isLoading, error };
}

import { useState } from "react";
import { apiFetch } from "@/lib/api-client";

export interface UseArchiveSessionResult {
  archiveSession: (sessionId: string) => Promise<void>;
  isArchiving: boolean;
  error?: string;
}

export function useArchiveSession(): UseArchiveSessionResult {
  const [isArchiving, setIsArchiving] = useState(false);
  const [error, setError] = useState<string | undefined>(undefined);

  const archiveSession = async (sessionId: string): Promise<void> => {
    setIsArchiving(true);
    setError(undefined);

    try {
      const response = await apiFetch(`/api/sessions/${encodeURIComponent(sessionId)}/retention`, {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ retentionStatus: "archived" }),
      });

      if (!response.ok) {
        const body = await response.json().catch(() => ({}));
        throw new Error((body as { error?: string }).error ?? `HTTP ${response.status}`);
      }
    } catch (err) {
      const message = err instanceof Error ? err.message : "Failed to archive session";
      setError(message);
      throw err;
    } finally {
      setIsArchiving(false);
    }
  };

  return { archiveSession, isArchiving, error };
}

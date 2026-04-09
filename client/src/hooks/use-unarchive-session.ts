import { useState } from "react";
import { apiFetch } from "@/lib/api-client";

export interface UseUnarchiveSessionResult {
  unarchiveSession: (sessionId: string) => Promise<void>;
  isUnarchiving: boolean;
  error?: string;
}

export function useUnarchiveSession(): UseUnarchiveSessionResult {
  const [isUnarchiving, setIsUnarchiving] = useState(false);
  const [error, setError] = useState<string | undefined>(undefined);

  const unarchiveSession = async (sessionId: string): Promise<void> => {
    setIsUnarchiving(true);
    setError(undefined);

    try {
      const response = await apiFetch(`/api/sessions/${encodeURIComponent(sessionId)}/retention`, {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ retentionStatus: "active" }),
      });

      if (!response.ok) {
        const body = await response.json().catch(() => ({}));
        throw new Error((body as { error?: string }).error ?? `HTTP ${response.status}`);
      }
    } catch (err) {
      const message = err instanceof Error ? err.message : "Failed to unarchive session";
      setError(message);
      throw err;
    } finally {
      setIsUnarchiving(false);
    }
  };

  return { unarchiveSession, isUnarchiving, error };
}

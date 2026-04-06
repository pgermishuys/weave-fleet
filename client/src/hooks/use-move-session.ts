
import { useState } from "react";
import { apiFetch } from "@/lib/api-client";

export interface UseMoveSessionResult {
  moveSession: (sessionId: string, projectId: string | null) => Promise<void>;
  isMoving: boolean;
  error?: string;
}

export function useMoveSession(): UseMoveSessionResult {
  const [isMoving, setIsMoving] = useState(false);
  const [error, setError] = useState<string | undefined>();

  const moveSession = async (
    sessionId: string,
    projectId: string | null
  ): Promise<void> => {
    setIsMoving(true);
    setError(undefined);

    try {
      const response = await apiFetch(
        `/api/sessions/${encodeURIComponent(sessionId)}/project`,
        {
          method: "PATCH",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ projectId }),
        }
      );

      if (!response.ok) {
        const body = await response.json().catch(() => ({}));
        throw new Error((body as { error?: string }).error ?? `HTTP ${response.status}`);
      }
    } catch (err) {
      const message = err instanceof Error ? err.message : "Failed to move session";
      setError(message);
      throw err;
    } finally {
      setIsMoving(false);
    }
  };

  return { moveSession, isMoving, error };
}

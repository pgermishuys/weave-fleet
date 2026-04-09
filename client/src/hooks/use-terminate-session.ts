
import { useState } from "react";
import { apiFetch } from "@/lib/api-client";

export interface TerminateSessionOptions {
  cleanupWorkspace?: boolean;
}

export interface UseTerminateSessionResult {
  terminateSession: (
    sessionId: string,
    instanceId: string,
    opts?: TerminateSessionOptions
  ) => Promise<void>;
  isTerminating: boolean;
  error?: string;
}

export function useTerminateSession(): UseTerminateSessionResult {
  const [isTerminating, setIsTerminating] = useState(false);
  const [error, setError] = useState<string | undefined>(undefined);

  const terminateSession = async (
    sessionId: string,
    instanceId: string,
    opts?: TerminateSessionOptions
  ): Promise<void> => {
    void instanceId;
    void opts;
    setIsTerminating(true);
    setError(undefined);

    try {
      const response = await apiFetch(
        `/api/sessions/${encodeURIComponent(sessionId)}/stop`,
        { method: "POST" }
      );

      if (!response.ok) {
        const body = await response.json().catch(() => ({}));
        throw new Error(
          (body as { error?: string }).error ?? `HTTP ${response.status}`
        );
      }
    } catch (err) {
      const message = err instanceof Error ? err.message : "Failed to terminate session";
      setError(message);
      throw err;
    } finally {
      setIsTerminating(false);
    }
  };

  return { terminateSession, isTerminating, error };
}

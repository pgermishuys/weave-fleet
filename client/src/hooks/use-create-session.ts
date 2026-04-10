
import { useState, useCallback } from "react";
import type { CreateSessionRequest, CreateSessionResponse, SessionSourceSelection } from "@/lib/api-types";
import { apiFetch } from "@/lib/api-client";

export interface CreateSessionOptions {
  title?: string;
  isolationStrategy?: "existing" | "worktree" | "clone";
  branch?: string;
  source?: SessionSourceSelection;
  /** Harness type to use for this session (e.g. "opencode", "claude"). */
  harnessType?: string;
  /** Optional project to assign this session to at creation time */
  projectId?: string;
}

export interface UseCreateSessionResult {
  createSession: (
    directory: string | undefined,
    opts?: CreateSessionOptions
  ) => Promise<CreateSessionResponse>;
  isLoading: boolean;
  error?: string;
}

export function useCreateSession(): UseCreateSessionResult {
  const [isLoading, setIsLoading] = useState(false);
  const [error, setError] = useState<string | undefined>();

  const createSession = useCallback(
    async (directory: string | undefined, opts?: CreateSessionOptions): Promise<CreateSessionResponse> => {
      setIsLoading(true);
      setError(undefined);
      try {
        const body: CreateSessionRequest = {
          directory,
          title: opts?.title,
          isolationStrategy: opts?.isolationStrategy,
          branch: opts?.branch,
          source: opts?.source,
          harnessType: opts?.harnessType,
          projectId: opts?.projectId,
        };

        const response = await apiFetch("/api/sessions", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(body),
        });

        if (!response.ok) {
          const data = await response.json().catch(() => ({}));
          const message = (data as { error?: string }).error ?? `HTTP ${response.status}`;
          setError(message);
          throw new Error(message);
        }

        return (await response.json()) as CreateSessionResponse;
      } finally {
        setIsLoading(false);
      }
    },
    []
  );

  return { createSession, isLoading, error };
}

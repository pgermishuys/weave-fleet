import { useCallback, useState } from "react";
import { apiFetch } from "@/lib/api-client";
import type {
  AddSessionSourceRequest,
  PreviewSessionSourceRequest,
  PreviewSessionSourceResponse,
  SessionSourcePreview,
  SessionSourceSelection,
} from "@/lib/api-types";

interface UseAddSourceToSessionResult {
  previewSource: (sessionId: string, source: SessionSourceSelection) => Promise<SessionSourcePreview>;
  addSourceToSession: (sessionId: string, source: SessionSourceSelection, confirm?: boolean) => Promise<void>;
  isLoading: boolean;
  error?: string;
}

export function useAddSourceToSession(): UseAddSourceToSessionResult {
  const [isLoading, setIsLoading] = useState(false);
  const [error, setError] = useState<string | undefined>();

  const previewSource = useCallback(async (sessionId: string, source: SessionSourceSelection) => {
    setIsLoading(true);
    setError(undefined);
    try {
      const body: PreviewSessionSourceRequest = { source };
      const response = await apiFetch(`/api/sessions/${encodeURIComponent(sessionId)}/source-preview`, {
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

      const data = (await response.json()) as PreviewSessionSourceResponse;
      return data.preview;
    } finally {
      setIsLoading(false);
    }
  }, []);

  const addSourceToSession = useCallback(async (sessionId: string, source: SessionSourceSelection, confirm = true) => {
    setIsLoading(true);
    setError(undefined);
    try {
      const body: AddSessionSourceRequest = { source, confirm };
      const response = await apiFetch(`/api/sessions/${encodeURIComponent(sessionId)}/sources`, {
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
    } finally {
      setIsLoading(false);
    }
  }, []);

  return { previewSource, addSourceToSession, isLoading, error };
}

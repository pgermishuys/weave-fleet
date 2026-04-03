"use client";

import { useState, useEffect } from "react";
import { apiFetch } from "@/lib/api-client";

export interface UseFindFilesResult {
  files: string[];
  isLoading: boolean;
  error?: string;
}

/**
 * Debounced file-search hook.
 * Only fires a fetch when query.length >= 1, after a 300ms debounce.
 * Cancels in-flight requests when the query changes.
 * Returns empty array immediately when query is empty.
 */
export function useFindFiles(instanceId: string, query: string): UseFindFilesResult {
  const [files, setFiles] = useState<string[]>([]);
  const [isLoading, setIsLoading] = useState(false);
  const [error, setError] = useState<string | undefined>();
  const [prevQuery, setPrevQuery] = useState(query);

  // Clear results synchronously during render when query is cleared (derived state pattern).
  const trimmedQuery = query.trim();
  if (query !== prevQuery) {
    setPrevQuery(query);
    if (trimmedQuery === "" && prevQuery.trim() !== "") {
      setFiles([]);
      setIsLoading(false);
      setError(undefined);
    }
  }

  useEffect(() => {
    if (!instanceId || !trimmedQuery) {
      return;
    }

    let cancelled = false;
    let controller: AbortController | null = null;

    const timer = setTimeout(() => {
      controller = new AbortController();
      setIsLoading(true);
      setError(undefined);

      const url = `/api/instances/${encodeURIComponent(instanceId)}/find/files?query=${encodeURIComponent(trimmedQuery)}`;
      apiFetch(url, { signal: controller.signal })
        .then(async (response) => {
          if (!response.ok) {
            const data = await response.json().catch(() => ({}));
            const message = (data as { error?: string }).error ?? `HTTP ${response.status}`;
            throw new Error(message);
          }
          return response.json() as Promise<string[]>;
        })
        .then((data) => {
          if (!cancelled) setFiles(data);
        })
        .catch((err: unknown) => {
          if (!cancelled && !(err instanceof DOMException && err.name === "AbortError")) {
            setError(err instanceof Error ? err.message : "Failed to search files");
          }
        })
        .finally(() => {
          if (!cancelled) setIsLoading(false);
        });
    }, 300);

    return () => {
      cancelled = true;
      clearTimeout(timer);
      controller?.abort();
    };
  }, [instanceId, trimmedQuery]);

  return { files, isLoading, error };
}

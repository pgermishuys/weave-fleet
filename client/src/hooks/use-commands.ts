"use client";

import { useState, useEffect } from "react";
import type { AutocompleteCommand } from "@/lib/api-types";
import { apiFetch } from "@/lib/api-client";

export interface UseCommandsResult {
  commands: AutocompleteCommand[];
  isLoading: boolean;
  error?: string;
}

/**
 * Fetches the list of available slash commands for an OpenCode instance.
 * Fetches once on mount (commands are static for a session lifetime).
 * Re-fetches if instanceId changes.
 */
export function useCommands(instanceId: string): UseCommandsResult {
  const [commands, setCommands] = useState<AutocompleteCommand[]>([]);
  const [isLoading, setIsLoading] = useState(!!instanceId);
  const [error, setError] = useState<string | undefined>();
  const [prevInstanceId, setPrevInstanceId] = useState(instanceId);

  // Reset loading state when instanceId changes (derived state pattern)
  if (instanceId !== prevInstanceId) {
    setPrevInstanceId(instanceId);
    if (instanceId) {
      setIsLoading(true);
      setError(undefined);
    }
  }

  useEffect(() => {
    if (!instanceId) return;

    let cancelled = false;
    const controller = new AbortController();

    async function fetchCommands() {
      try {
        const response = await apiFetch(
          `/api/instances/${encodeURIComponent(instanceId)}/commands`,
          { signal: controller.signal }
        );
        if (!response.ok) {
          const data = await response.json().catch(() => ({}));
          const message = (data as { error?: string }).error ?? `HTTP ${response.status}`;
          throw new Error(message);
        }
        const data = (await response.json()) as AutocompleteCommand[];
        if (!cancelled) {
          setCommands(data);
          setError(undefined);
        }
      } catch (err: unknown) {
        if (!cancelled && !(err instanceof DOMException && err.name === "AbortError")) {
          setError(err instanceof Error ? err.message : "Failed to load commands");
        }
      } finally {
        if (!cancelled) setIsLoading(false);
      }
    }

    void fetchCommands();

    return () => {
      cancelled = true;
      controller.abort();
    };
  }, [instanceId]);

  return { commands, isLoading, error };
}

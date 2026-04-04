"use client";

import { useState, useEffect, useCallback } from "react";
import type { ProjectResponse } from "@/lib/api-types";
import { apiFetch } from "@/lib/api-client";

export interface UseProjectsResult {
  projects: ProjectResponse[];
  isLoading: boolean;
  error?: string;
  refetch: () => void;
}

export interface UseProjectsOptions {
  /** When false, skip the initial fetch. Defaults to true. */
  enabled?: boolean;
}

export function useProjects(options?: UseProjectsOptions): UseProjectsResult {
  const enabled = options?.enabled ?? true;
  const [projects, setProjects] = useState<ProjectResponse[]>([]);
  const [isLoading, setIsLoading] = useState(enabled);
  const [error, setError] = useState<string | undefined>();

  const fetchProjects = useCallback(async () => {
    try {
      const response = await apiFetch("/api/projects");
      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }
      const data = (await response.json()) as ProjectResponse[];
      setProjects(data);
      setError(undefined);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setIsLoading(false);
    }
  }, []);

  useEffect(() => {
    if (enabled) fetchProjects();
  }, [enabled, fetchProjects]);

  return { projects, isLoading, error, refetch: fetchProjects };
}

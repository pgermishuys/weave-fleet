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

export function useProjects(): UseProjectsResult {
  const [projects, setProjects] = useState<ProjectResponse[]>([]);
  const [isLoading, setIsLoading] = useState(true);
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
    fetchProjects();
  }, [fetchProjects]);

  return { projects, isLoading, error, refetch: fetchProjects };
}

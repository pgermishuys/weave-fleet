
import { useState } from "react";
import type { CreateProjectRequest, ProjectResponse } from "@/lib/api-types";
import { apiFetch } from "@/lib/api-client";

export interface UseCreateProjectResult {
  createProject: (request: CreateProjectRequest) => Promise<ProjectResponse>;
  isCreating: boolean;
  error?: string;
}

export function useCreateProject(): UseCreateProjectResult {
  const [isCreating, setIsCreating] = useState(false);
  const [error, setError] = useState<string | undefined>();

  const createProject = async (request: CreateProjectRequest): Promise<ProjectResponse> => {
    setIsCreating(true);
    setError(undefined);

    try {
      const response = await apiFetch("/api/projects", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(request),
      });

      if (!response.ok) {
        const body = await response.json().catch(() => ({}));
        throw new Error((body as { error?: string }).error ?? `HTTP ${response.status}`);
      }

      return (await response.json()) as ProjectResponse;
    } catch (err) {
      const message = err instanceof Error ? err.message : "Failed to create project";
      setError(message);
      throw err;
    } finally {
      setIsCreating(false);
    }
  };

  return { createProject, isCreating, error };
}

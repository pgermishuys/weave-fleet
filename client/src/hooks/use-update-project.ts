
import { useState } from "react";
import type { UpdateProjectRequest, ProjectResponse } from "@/lib/api-types";
import { apiFetch } from "@/lib/api-client";

export interface UseUpdateProjectResult {
  updateProject: (projectId: string, request: UpdateProjectRequest) => Promise<ProjectResponse>;
  isUpdating: boolean;
  error?: string;
}

export function useUpdateProject(): UseUpdateProjectResult {
  const [isUpdating, setIsUpdating] = useState(false);
  const [error, setError] = useState<string | undefined>();

  const updateProject = async (
    projectId: string,
    request: UpdateProjectRequest
  ): Promise<ProjectResponse> => {
    setIsUpdating(true);
    setError(undefined);

    try {
      const response = await apiFetch(`/api/projects/${encodeURIComponent(projectId)}`, {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(request),
      });

      if (!response.ok) {
        const body = await response.json().catch(() => ({}));
        throw new Error((body as { error?: string }).error ?? `HTTP ${response.status}`);
      }

      return (await response.json()) as ProjectResponse;
    } catch (err) {
      const message = err instanceof Error ? err.message : "Failed to update project";
      setError(message);
      throw err;
    } finally {
      setIsUpdating(false);
    }
  };

  return { updateProject, isUpdating, error };
}

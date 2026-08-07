import { readonly, shallowRef, type ShallowRef } from "vue";
import type { components } from "@/api/generated/schema";
import { api } from "@/api/client";

export interface UseRenameWorkspaceResult {
  renameWorkspace: (workspaceId: string, displayName: string, onSuccess?: () => void) => Promise<void>;
  isLoading: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | undefined>>;
}

export function useRenameWorkspace(): UseRenameWorkspaceResult {
  const isLoading = shallowRef(false);
  const error = shallowRef<string | undefined>(undefined);

  async function renameWorkspace(
    workspaceId: string,
    displayName: string,
    onSuccess?: () => void,
  ): Promise<void> {
    isLoading.value = true;
    error.value = undefined;

    try {
      const { error: apiError, response } = await api.PATCH("/api/workspaces/{id}", {
        params: {
          path: { id: workspaceId },
        },
        body: { displayName } as components["schemas"]["RenameWorkspaceRequest"],
      });

      if (apiError || !response.ok) {
        const payload = apiError as { error?: string } | undefined;
        throw new Error(payload?.error ?? `HTTP ${response.status}`);
      }

      onSuccess?.();
    } catch (renameError) {
      error.value = renameError instanceof Error ? renameError.message : "Failed to rename workspace";
      throw renameError;
    } finally {
      isLoading.value = false;
    }
  }

  return {
    renameWorkspace,
    isLoading: readonly(isLoading),
    error: readonly(error),
  };
}

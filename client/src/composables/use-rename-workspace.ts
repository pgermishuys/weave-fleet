import { readonly, shallowRef, type ShallowRef } from "vue";
import { apiFetch } from "@/lib/api-client";

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
      const response = await apiFetch(`/api/workspaces/${encodeURIComponent(workspaceId)}`, {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ displayName }),
      });

      if (!response.ok) {
        const body = (await response.json().catch(() => ({}))) as { error?: string };
        throw new Error(body.error ?? `HTTP ${response.status}`);
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

import { readonly, shallowRef, watch, type Ref, type ShallowRef } from "vue";
import { api } from "@/api/client";
import type { WorktreeInfo, RepositoryWorktreesResponse } from "@/api/client";

interface UseWorktreesOptions {
  /** Reactive repository path — worktrees are fetched when this changes. */
  repositoryPath: Ref<string | null> | ShallowRef<string | null>;
  /** Only fetch when this is true (e.g. when the dialog is open). */
  enabled?: Ref<boolean> | ShallowRef<boolean>;
}

interface UseWorktreesResult {
  worktrees: Readonly<ShallowRef<readonly WorktreeInfo[]>>;
  isLoading: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | null>>;
}

export function useWorktrees(options: UseWorktreesOptions): UseWorktreesResult {
  const worktrees = shallowRef<readonly WorktreeInfo[]>([]);
  const isLoading = shallowRef(false);
  const error = shallowRef<string | null>(null);

  async function fetchWorktrees(path: string): Promise<void> {
    isLoading.value = true;
    error.value = null;

    try {
      const { data, error: apiError } = await api.GET("/api/repositories/worktrees", {
        params: { query: { path } },
      });

      if (apiError) {
        throw new Error(String(apiError));
      }

      if (!data) {
        throw new Error("No data returned");
      }

      const responseData = data as RepositoryWorktreesResponse;
      worktrees.value = responseData.worktrees;
    } catch (fetchError) {
      error.value = fetchError instanceof Error ? fetchError.message : "Unknown error";
      worktrees.value = [];
    } finally {
      isLoading.value = false;
    }
  }

  watch(
    [options.repositoryPath, ...(options.enabled ? [options.enabled] : [])],
    ([path, enabled]) => {
      const isEnabled = options.enabled ? (enabled as boolean) : true;
      if (isEnabled && path) {
        void fetchWorktrees(path);
      } else {
        worktrees.value = [];
        error.value = null;
      }
    },
    { immediate: true },
  );

  return {
    worktrees: readonly(worktrees),
    isLoading: readonly(isLoading),
    error: readonly(error),
  };
}

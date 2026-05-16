import { readonly, shallowRef, watch, type Ref, type ShallowRef } from "vue";
import { apiFetch } from "@/lib/api-client";
import type { WorktreeInfo, RepositoryWorktreesResponse } from "@/lib/api-types";

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
      const response = await apiFetch(
        `/api/repositories/worktrees?path=${encodeURIComponent(path)}`,
        { method: "GET" },
      );

      if (!response.ok) {
        const data = (await response.json().catch(() => ({}))) as { error?: string };
        throw new Error(data.error ?? "Failed to load worktrees");
      }

      const data = (await response.json()) as RepositoryWorktreesResponse;
      worktrees.value = data.worktrees;
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

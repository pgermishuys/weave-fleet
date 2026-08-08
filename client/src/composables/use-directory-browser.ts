import { computed, onUnmounted, readonly, ref, shallowRef, type ComputedRef, type Ref, type ShallowRef } from "vue";
import type { DirectoryEntry, DirectoryListResponse } from "@/api/client";
import { api } from "@/api/client";

export interface UseDirectoryBrowserResult {
  currentPath: Readonly<ShallowRef<string | null>>;
  entries: Readonly<Ref<readonly DirectoryEntry[]>>;
  isLoading: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | undefined>>;
  roots: Readonly<Ref<readonly string[]>>;
  parentPath: Readonly<ShallowRef<string | null>>;
  search: Readonly<ShallowRef<string>>;
  browse: (path: string | null) => void;
  goUp: () => void;
  refresh: () => Promise<void>;
  setSearch: (value: string) => void;
  hasActivated: Readonly<ShallowRef<boolean>>;
  canBrowse: ComputedRef<boolean>;
}

export interface UseDirectoryBrowserOptions {
  unconstrained?: boolean;
}

export function useDirectoryBrowser(enabled = false, options: UseDirectoryBrowserOptions = {}): UseDirectoryBrowserResult {
  const currentPath = shallowRef<string | null>(null);
  const entries = ref<DirectoryEntry[]>([]);
  const isLoading = shallowRef(false);
  const error = shallowRef<string | undefined>(undefined);
  const roots = ref<string[]>([]);
  const parentPath = shallowRef<string | null>(null);
  const search = shallowRef("");
  const hasActivated = shallowRef(false);
  const canBrowse = computed(() => enabled || hasActivated.value);

  let timeoutId: ReturnType<typeof setTimeout> | undefined;
  let controller: AbortController | undefined;
  let refreshToken = 0;

  function cleanupPending(): void {
    if (timeoutId) {
      clearTimeout(timeoutId);
      timeoutId = undefined;
    }

    controller?.abort();
    controller = undefined;
  }

  async function fetchDirectory(requestRefreshToken: number): Promise<void> {
    if (!canBrowse.value) {
      return;
    }

    controller = new AbortController();
    isLoading.value = true;
    error.value = undefined;

    const queryParams: Record<string, string> = {};
    if (currentPath.value !== null) {
      queryParams.path = currentPath.value;
    }

    const trimmedSearch = search.value.trim();
    if (trimmedSearch) {
      queryParams.search = trimmedSearch;
    }

    if (options.unconstrained) {
      queryParams.unconstrained = "true";
    }

    try {
      const { data, error: apiError } = await api.GET("/api/directories", {
        params: { query: queryParams },
        signal: controller.signal,
      });

      if (apiError) {
        throw new Error(String(apiError));
      }

      if (!data) {
        throw new Error("No data returned");
      }

      const responseData = data as DirectoryListResponse;
      if (requestRefreshToken !== refreshToken) {
        return;
      }

      currentPath.value = responseData.currentPath ?? null;
      entries.value = responseData.entries ?? [];
      roots.value = responseData.roots ?? [];
      parentPath.value = responseData.parentPath ?? null;
    } catch (fetchError) {
      if (fetchError instanceof DOMException && fetchError.name === "AbortError") {
        return;
      }

      if (requestRefreshToken !== refreshToken) {
        return;
      }

      error.value = fetchError instanceof Error ? fetchError.message : "Failed to browse directories";
    } finally {
      if (requestRefreshToken === refreshToken) {
        isLoading.value = false;
      }
      controller = undefined;
    }
  }

  async function queueFetch(): Promise<void> {
    cleanupPending();

    if (!canBrowse.value) {
      return;
    }

    const requestRefreshToken = ++refreshToken;
    const delay = search.value.trim() ? 200 : 0;

    if (delay === 0) {
      return fetchDirectory(requestRefreshToken);
    }

    return new Promise((resolve) => {
      timeoutId = setTimeout(() => {
        void fetchDirectory(requestRefreshToken).finally(resolve);
      }, delay);
    });
  }

  function browse(path: string | null): void {
    hasActivated.value = true;
    currentPath.value = path;
    search.value = "";
    isLoading.value = true;
    void queueFetch();
  }

  function goUp(): void {
    currentPath.value = parentPath.value;
    search.value = "";
    isLoading.value = true;
    void queueFetch();
  }

  async function refresh(): Promise<void> {
    await queueFetch();
  }

  function setSearch(value: string): void {
    search.value = value;
    void queueFetch();
  }

  onUnmounted(() => {
    cleanupPending();
  });

  return {
    currentPath: readonly(currentPath),
    entries: readonly(entries),
    isLoading: readonly(isLoading),
    error: readonly(error),
    roots: readonly(roots),
    parentPath: readonly(parentPath),
    search: readonly(search),
    browse,
    goUp,
    refresh,
    setSearch,
    hasActivated: readonly(hasActivated),
    canBrowse,
  };
}

import { shallowRef, watch, type ShallowRef } from "vue";
import { apiFetch } from "@/lib/api-client";
import type { RepositoryInfo, RepositoryInfoResponse } from "@/lib/api-types";

export interface UseRepositoryInfoResult {
  info: ShallowRef<RepositoryInfo | null>;
  isLoading: ShallowRef<boolean>;
  error: ShallowRef<string | null>;
}

export function useRepositoryInfo(path: string | null): UseRepositoryInfoResult {
  const info = shallowRef<RepositoryInfo | null>(null);
  const isLoading = shallowRef(false);
  const error = shallowRef<string | null>(null);

  let controller: AbortController | undefined;

  watch(
    () => path,
    async (nextPath) => {
      controller?.abort();

      if (nextPath === null) {
        info.value = null;
        isLoading.value = false;
        error.value = null;
        return;
      }

      controller = new AbortController();
      isLoading.value = true;
      error.value = null;
      info.value = null;

      try {
        const response = await apiFetch(`/api/repositories/info?path=${encodeURIComponent(nextPath)}`, {
          signal: controller.signal,
        });

        if (!response.ok) {
          const data = (await response.json().catch(() => ({}))) as { error?: string };
          throw new Error(data.error ?? "Failed to load repository info");
        }

        const data = (await response.json()) as RepositoryInfoResponse;
        info.value = data.repository;
      } catch (fetchError) {
        if (fetchError instanceof DOMException && fetchError.name === "AbortError") {
          return;
        }

        error.value = fetchError instanceof Error ? fetchError.message : "Unknown error";
      } finally {
        isLoading.value = false;
      }
    },
    { immediate: true },
  );

  return {
    info,
    isLoading,
    error,
  };
}

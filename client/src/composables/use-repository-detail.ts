import { shallowRef, watch, type ShallowRef } from "vue";
import { apiFetch } from "@/lib/api-client";
import type { RepositoryDetail, RepositoryDetailResponse } from "@/lib/api-types";

export interface UseRepositoryDetailResult {
  detail: ShallowRef<RepositoryDetail | null>;
  isLoading: ShallowRef<boolean>;
  error: ShallowRef<string | null>;
}

export function useRepositoryDetail(path: string | null): UseRepositoryDetailResult {
  const detail = shallowRef<RepositoryDetail | null>(null);
  const isLoading = shallowRef(false);
  const error = shallowRef<string | null>(null);

  let controller: AbortController | undefined;

  watch(
    () => path,
    async (nextPath) => {
      controller?.abort();

      if (nextPath === null) {
        detail.value = null;
        isLoading.value = false;
        error.value = null;
        return;
      }

      controller = new AbortController();
      isLoading.value = true;
      error.value = null;
      detail.value = null;

      try {
        const response = await apiFetch(`/api/repositories/detail?path=${encodeURIComponent(nextPath)}`, {
          signal: controller.signal,
        });

        if (!response.ok) {
          const data = (await response.json().catch(() => ({}))) as { error?: string };
          throw new Error(data.error ?? "Failed to load repository detail");
        }

        const data = (await response.json()) as RepositoryDetailResponse;
        detail.value = data.repository;
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
    detail,
    isLoading,
    error,
  };
}

import { shallowRef, watch, type ShallowRef } from "vue";
import { api } from "@/api/client";
import type { RepositoryDetail, RepositoryDetailResponse } from "@/api/client";

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
        const { data, error: apiError } = await api.GET("/api/repositories/detail", {
          params: { query: { path: nextPath } },
          signal: controller.signal,
        });

        if (apiError) {
          throw new Error(String(apiError));
        }

        if (!data) {
          throw new Error("No data returned");
        }

        const responseData = data as RepositoryDetailResponse;
        detail.value = responseData.repository;
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

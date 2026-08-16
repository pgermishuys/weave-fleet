import { readonly, ref, shallowRef, type Ref, type ShallowRef } from "vue";
import { api } from "@/api/client";
import { extractApiError } from "@/lib/api-error";

export interface KeyFilesResponse {
  filesByTool: Record<string, readonly string[]>;
}

export interface UseKeyFilesResult {
  filesByTool: Readonly<Ref<Record<string, readonly string[]>>>;
  isLoading: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | undefined>>;
  fetch: (directory: string) => Promise<void>;
}

export function useKeyFiles(): UseKeyFilesResult {
  const filesByTool = ref<Record<string, readonly string[]>>({});
  const isLoading = shallowRef(false);
  const error = shallowRef<string | undefined>(undefined);

  async function fetch(directory: string): Promise<void> {
    isLoading.value = true;
    error.value = undefined;

    try {
      const { data, error: apiError } = await api.GET("/api/key-files", {
        params: { query: { directory } },
      });

      if (apiError) {
        throw new Error(extractApiError(apiError, "Failed to load key files"));
      }

      if (!data) {
        throw new Error("No data returned");
      }

      const result = data as KeyFilesResponse;
      filesByTool.value = result.filesByTool ?? {};
    } catch (fetchError) {
      error.value = fetchError instanceof Error ? fetchError.message : "Failed to load key files";
    } finally {
      isLoading.value = false;
    }
  }

  return {
    filesByTool: readonly(filesByTool),
    isLoading: readonly(isLoading),
    error: readonly(error),
    fetch,
  };
}

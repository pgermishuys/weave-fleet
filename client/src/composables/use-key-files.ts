import { readonly, ref, shallowRef, type Ref, type ShallowRef } from "vue";
import { apiFetch } from "@/lib/api-client";

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
      const response = await apiFetch(
        `/api/key-files?directory=${encodeURIComponent(directory)}`,
      );

      if (!response.ok) {
        const body = (await response.json().catch(() => ({}))) as { error?: string };
        throw new Error(body.error ?? `HTTP ${response.status}`);
      }

      const data = (await response.json()) as KeyFilesResponse;
      filesByTool.value = data.filesByTool ?? {};
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

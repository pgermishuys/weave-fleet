import { readonly, shallowRef, type ShallowRef } from "vue";
import { api } from "@/api/client";

export type OpenTool = string;

export interface UseOpenDirectoryResult {
  openDirectory: (directory: string, tool: OpenTool) => Promise<void>;
  isOpening: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | undefined>>;
}

export function useOpenDirectory(): UseOpenDirectoryResult {
  const isOpening = shallowRef(false);
  const error = shallowRef<string | undefined>(undefined);

  async function openDirectory(directory: string, tool: OpenTool): Promise<void> {
    isOpening.value = true;
    error.value = undefined;

    try {
      const { error: apiError } = await api.POST("/api/open-directory", {
        body: { directory, tool } as never,
      });

      if (apiError) {
        throw new Error(String(apiError));
      }
    } catch (openError) {
      error.value = openError instanceof Error ? openError.message : "Failed to open directory";
      throw openError;
    } finally {
      isOpening.value = false;
    }
  }

  return {
    openDirectory,
    isOpening: readonly(isOpening),
    error: readonly(error),
  };
}

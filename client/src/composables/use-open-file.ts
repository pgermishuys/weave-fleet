import { readonly, shallowRef, type ShallowRef } from "vue";
import { api } from "@/api/client";

export interface UseOpenFileResult {
  openFile: (filePath: string, tool: string) => Promise<void>;
  isOpening: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | undefined>>;
}

export function useOpenFile(): UseOpenFileResult {
  const isOpening = shallowRef(false);
  const error = shallowRef<string | undefined>(undefined);

  async function openFile(filePath: string, tool: string): Promise<void> {
    isOpening.value = true;
    error.value = undefined;

    try {
      const { error: apiError } = await api.POST("/api/open-file", {
        body: { filePath, tool } as never,
      });

      if (apiError) {
        throw new Error(String(apiError));
      }
    } catch (openError) {
      error.value = openError instanceof Error ? openError.message : "Failed to open file";
      throw openError;
    } finally {
      isOpening.value = false;
    }
  }

  return {
    openFile,
    isOpening: readonly(isOpening),
    error: readonly(error),
  };
}

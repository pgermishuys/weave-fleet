import { readonly, shallowRef, type ShallowRef } from "vue";
import { apiFetch } from "@/lib/api-client";

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
      const response = await apiFetch("/api/open-file", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ filePath, tool }),
      });

      if (!response.ok) {
        const body = (await response.json().catch(() => ({}))) as { error?: string };
        throw new Error(body.error ?? `HTTP ${response.status}`);
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

import { readonly, shallowRef, type ShallowRef } from "vue";
import { apiFetch } from "@/lib/api-client";

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
      const response = await apiFetch("/api/open-directory", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ directory, tool }),
      });

      if (!response.ok) {
        const body = (await response.json().catch(() => ({}))) as { error?: string };
        throw new Error(body.error ?? `HTTP ${response.status}`);
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

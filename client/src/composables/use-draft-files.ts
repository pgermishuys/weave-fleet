import { reactive, computed, type ComputedRef } from "vue";

export interface DraftFile {
  id: string;
  file: File;
  name: string;
  size: number;
}

const fileRegistry = reactive<Record<string, DraftFile[]>>({});

function ensureEntry(sessionId: string): DraftFile[] {
  if (!fileRegistry[sessionId]) {
    fileRegistry[sessionId] = [];
  }
  return fileRegistry[sessionId];
}

/** Files attached to a session's draft, kept until they're sent. They upload on send, not when added. */
export function useDraftFiles(sessionId: string) {
  const files: ComputedRef<DraftFile[]> = computed(() => ensureEntry(sessionId));

  function addFile(file: File): void {
    ensureEntry(sessionId).push({ id: crypto.randomUUID(), file, name: file.name, size: file.size });
  }

  function removeFile(id: string): void {
    const list = ensureEntry(sessionId);
    const index = list.findIndex((f) => f.id === id);
    if (index >= 0) list.splice(index, 1);
  }

  function clearFiles(): void {
    fileRegistry[sessionId] = [];
  }

  return { files, addFile, removeFile, clearFiles };
}

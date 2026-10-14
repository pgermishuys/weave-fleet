import { readLineCount } from "@/lib/attached-files";
import { reactive, computed, type ComputedRef } from "vue";

export interface DraftFile {
  id: string;
  file: File;
  name: string;
  size: number;
  /** Lines in a text file, once counted. */
  lines?: number;
  /** Text the user pasted and chose to attach: it always uploads. */
  pasted?: boolean;
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

  function addFile(file: File, pasted = false): void {
    const list = ensureEntry(sessionId);
    list.push({ id: crypto.randomUUID(), file, name: file.name, size: file.size, pasted });
    const entry = list[list.length - 1];
    // Counted after it's shown; a file that was removed meanwhile is left alone.
    void readLineCount(file).then((lines) => {
      if (lines !== undefined && ensureEntry(sessionId).includes(entry)) entry.lines = lines;
    }, () => undefined);
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

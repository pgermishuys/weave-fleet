import { defineStore } from "pinia";
import { shallowRef } from "vue";

/**
 * Go to file (Ctrl P): which session's picker is open, and which file should take focus once it opens. A file opened
 * from a message's link takes focus the same way, at the line the message named.
 */
export const useGoToFileStore = defineStore("go-to-file", () => {
  const sessionId = shallowRef<string | null>(null);
  const focusOnOpen = shallowRef<{ sessionId: string; path: string; line?: number } | null>(null);

  function show(id: string): void {
    sessionId.value = id;
  }

  function hide(): void {
    sessionId.value = null;
  }

  /** The file canvas calls this when it attaches; the pick once, for the file that was just picked, else null. */
  function takeFocus(id: string, path: string): { line?: number } | null {
    const pending = focusOnOpen.value;
    if (pending?.sessionId !== id || pending.path !== path) return null;
    focusOnOpen.value = null;
    return { line: pending.line };
  }

  return { sessionId, focusOnOpen, show, hide, takeFocus };
});

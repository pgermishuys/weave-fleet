import { defineStore } from "pinia";
import { shallowRef } from "vue";

/** Go to file (Ctrl P): which session's picker is open, and which file should take focus once it opens. */
export const useGoToFileStore = defineStore("go-to-file", () => {
  const sessionId = shallowRef<string | null>(null);
  const focusOnOpen = shallowRef<{ sessionId: string; path: string } | null>(null);

  function show(id: string): void {
    sessionId.value = id;
  }

  function hide(): void {
    sessionId.value = null;
  }

  /** The file canvas calls this when it attaches; true once, for the file that was just picked. */
  function takeFocus(id: string, path: string): boolean {
    const pending = focusOnOpen.value;
    if (pending?.sessionId !== id || pending.path !== path) return false;
    focusOnOpen.value = null;
    return true;
  }

  return { sessionId, focusOnOpen, show, hide, takeFocus };
});

import { defineStore } from "pinia";
import { shallowRef } from "vue";

/**
 * Find in files (Ctrl Shift F): which session's Files tab was asked to search inside files. The tab takes the request
 * once it is on screen, switches to "File contents" and puts the cursor in the field.
 */
export const useFindInFilesStore = defineStore("find-in-files", () => {
  const request = shallowRef<{ sessionId: string } | null>(null);

  function ask(sessionId: string): void {
    request.value = { sessionId };
  }

  /** True once, for the session that was asked. */
  function take(sessionId: string): boolean {
    if (request.value?.sessionId !== sessionId) return false;
    request.value = null;
    return true;
  }

  return { request, ask, take };
});

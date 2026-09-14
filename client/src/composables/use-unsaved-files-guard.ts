import { useEventListener } from "@vueuse/core";
import { useFileBuffersStore } from "@/stores/file-buffers";

/**
 * While any open file has unsaved changes, leaving or reloading the page asks first. Unsaved
 * buffers don't survive a reload.
 */
export function useUnsavedFilesGuard(): void {
  const buffers = useFileBuffersStore();

  useEventListener(window, "beforeunload", (event: BeforeUnloadEvent) => {
    if (buffers.unsaved.length === 0) return;
    event.preventDefault();
    // Older browsers show the prompt only when returnValue is set.
    event.returnValue = "";
  });
}

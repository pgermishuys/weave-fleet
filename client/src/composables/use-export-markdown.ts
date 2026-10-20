import { readonly, shallowRef } from "vue";

/**
 * Which session's "Export as Markdown" dialog is open, if any. The ⋯ menu and the palette command both ask for it, and
 * the session page shows the one dialog.
 */
const exportingSessionId = shallowRef<string | null>(null);

export function openExportMarkdown(sessionId: string): void {
  exportingSessionId.value = sessionId;
}

export function closeExportMarkdown(): void {
  exportingSessionId.value = null;
}

export function useExportMarkdown() {
  return { sessionId: readonly(exportingSessionId), open: openExportMarkdown, close: closeExportMarkdown };
}

/**
 * Provide/inject contract for a file-oriented canvas (Changes, Files).
 * Each canvas provides its own context, so selection and the viewer
 * payload are scoped to that canvas rather than shared across the panel.
 */
import {
  type InjectionKey,
  type Ref,
  type ShallowRef,
  inject,
  provide,
  shallowRef,
  watch,
} from "vue";
import type { VisualPayload } from "@/lib/visual-payload";

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

export type ContentViewMode = "file" | "diff";

const FILES_TREE_WIDTH_KEY = "weave:files-tree-width";
const DEFAULT_FILES_TREE_WIDTH = 260;

export interface FilesExplorerContext {
  selectedFilePath: string | null;
  expandedDirs: Set<string>;
  searchQuery: string;
  scrollTop: number;
  filesTreeWidth: number;
}

// ---------------------------------------------------------------------------
// Context shape
// ---------------------------------------------------------------------------

export interface ContentPanelContext {
  /**
   * Files explorer context.
   */
  filesContext: Readonly<ShallowRef<FilesExplorerContext>>;

  /**
   * Content-viewer mode: "file" renders the file preview; "diff" renders the diff.
   * Defaults to "file" and resets to "file" on selection change.
   */
  viewMode: Readonly<ShallowRef<ContentViewMode>>;

  /**
   * The rendered content of the selected file, or null while nothing is loaded.
   */
  filePayload: Readonly<ShallowRef<VisualPayload | null>>;

  /**
   * Select a file. Also resets viewMode to "file".
   */
  selectFile: (path: string) => void;

  /**
   * Show loaded file content in this canvas's viewer.
   */
  showFile: (payload: VisualPayload) => void;

  /**
   * Close the viewer: clears the selection and the loaded content.
   */
  clearFile: () => void;

  /**
   * Set the content-viewer mode explicitly.
   */
  setViewMode: (mode: ContentViewMode) => void;

  /**
   * Update files explorer context.
   */
  updateFilesContext: (patch: Partial<FilesExplorerContext>) => void;
}

// ---------------------------------------------------------------------------
// Injection key + helpers
// ---------------------------------------------------------------------------

export const ContentPanelContextKey: InjectionKey<ContentPanelContext> = Symbol("ContentPanelContext");

export function provideContentPanelContext(sessionId: Readonly<Ref<string | null>>): ContentPanelContext {
  // State
  const filesContext = shallowRef<FilesExplorerContext>({
    selectedFilePath: null,
    expandedDirs: new Set(),
    searchQuery: "",
    scrollTop: 0,
    filesTreeWidth: readFilesTreeWidth(),
  });

  const viewMode = shallowRef<ContentViewMode>("file");
  const filePayload = shallowRef<VisualPayload | null>(null);

  // Persist files tree width to localStorage
  watch(
    () => filesContext.value.filesTreeWidth,
    (width) => {
      persistFilesTreeWidth(width);
    },
  );

  // Reset transient state on session change
  watch(sessionId, (newId, oldId) => {
    if (newId !== oldId && newId !== null) {
      filesContext.value = {
        selectedFilePath: null,
        expandedDirs: new Set(),
        searchQuery: "",
        scrollTop: 0,
        filesTreeWidth: readFilesTreeWidth(),
      };
      viewMode.value = "file";
      filePayload.value = null;
    }
  });

  // Actions
  function selectFile(path: string): void {
    filesContext.value = {
      ...filesContext.value,
      selectedFilePath: path,
    };
    // Reset to file view on every selection change.
    viewMode.value = "file";
  }

  function showFile(payload: VisualPayload): void {
    filePayload.value = payload;
  }

  function clearFile(): void {
    filesContext.value = {
      ...filesContext.value,
      selectedFilePath: null,
    };
    filePayload.value = null;
    viewMode.value = "file";
  }

  function setViewMode(mode: ContentViewMode): void {
    viewMode.value = mode;
  }

  function updateFilesContext(patch: Partial<FilesExplorerContext>): void {
    const { expandedDirs, ...rest } = patch;
    filesContext.value = {
      ...filesContext.value,
      ...rest,
      // Preserve Set identity if not patched
      expandedDirs: expandedDirs ?? filesContext.value.expandedDirs,
    };
  }

  // Helper: read files tree width from localStorage
  function readFilesTreeWidth(): number {
    if (typeof window === "undefined" || typeof localStorage === "undefined") {
      return DEFAULT_FILES_TREE_WIDTH;
    }

    try {
      const raw = localStorage.getItem(FILES_TREE_WIDTH_KEY);
      const parsed = raw === null ? Number.NaN : Number.parseFloat(raw);
      if (Number.isFinite(parsed) && parsed > 0) {
        return parsed;
      }
      return DEFAULT_FILES_TREE_WIDTH;
    } catch {
      return DEFAULT_FILES_TREE_WIDTH;
    }
  }

  // Helper: persist files tree width to localStorage
  function persistFilesTreeWidth(width: number): void {
    if (typeof window === "undefined" || typeof localStorage === "undefined") {
      return;
    }

    try {
      localStorage.setItem(FILES_TREE_WIDTH_KEY, String(width));
    } catch {
      // localStorage unavailable
    }
  }

  const ctx: ContentPanelContext = {
    filesContext,
    viewMode,
    filePayload,
    selectFile,
    showFile,
    clearFile,
    setViewMode,
    updateFilesContext,
  };

  provide(ContentPanelContextKey, ctx);

  return ctx;
}

export function useContentPanelContext(): ContentPanelContext {
  const ctx = inject(ContentPanelContextKey);
  if (!ctx) {
    throw new Error(
      "useContentPanelContext() was called outside a component that provides ContentPanelContext. "
      + "Make sure a parent renders the content panel provider.",
    );
  }

  return ctx;
}
